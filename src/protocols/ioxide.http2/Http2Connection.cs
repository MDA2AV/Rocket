using System.Buffers;
using System.IO.Pipelines;

namespace ioxide.http2;

/// <summary>
/// Serves HTTP/2 over one accepted <see cref="TcpConnection"/>, in managed code end to end: frames,
/// HPACK and flow control are all here, with no native library involved.
///
/// <code>
/// reactor.TcpHandle = (r, conn) =>
///     new Http2Connection(conn).RunBufferedAsync(request => Http2Response.Text("hello"));
/// </code>
///
/// A drop-in alternative to <c>Nghttp2Connection</c> - same shape, same request and response
/// surface - the way <c>ioxide.http3</c> is for <c>ioxide.nghttp3</c>. Take this one when shipping
/// a native library is inconvenient; take nghttp2 when you want the reference implementation's
/// coverage of the protocol's darker corners.
///
/// Like its nghttp2 counterpart it speaks to an <see cref="IDuplexPipe"/> and knows nothing about
/// TLS: hand it a <c>TcpConnectionDualPipe</c> for h2c or a <c>TlsConnectionDualPipe</c> for h2
/// over TLS, and the protocol code is identical either way.
/// </summary>
/// <remarks>Reactor thread only.</remarks>
public sealed partial class Http2Connection : IDisposable
{
    private readonly IDuplexPipe _pipe;
    private readonly Http2Options _options;
    private readonly HpackDecoder _decoder;

    // Inbound bytes accumulate here because a frame can straddle recv buffers - the ring hands out
    // whatever the kernel filled, which has nothing to do with frame boundaries.
    private byte[] _inbound = [];
    private int _inboundUsed;

    // Scratch for one header block's decoded literals. Reused per block; the request arena copies
    // out of it, so nothing here outlives the decode.
    private byte[] _headerScratch = new byte[16 * 1024];

    private readonly Dictionary<int, PendingRequest> _streams = new();
    private readonly List<PendingRequest> _ready = [];

    private bool _prefaceSeen;
    private bool _disposed;
    private bool _failed;

    // The peer's flow-control windows, as WE must respect them when sending. 65535 until its
    // SETTINGS say otherwise, which is the RFC's default rather than a guess.
    private int _peerConnectionWindow = 65535;
    private int _peerInitialStreamWindow = 65535;
    private int _peerMaxFrameSize = 16384;

    /// <summary>
    /// Serve over an already-chosen transport. The pipe is the caller's to dispose.
    /// </summary>
    public Http2Connection(IDuplexPipe pipe, Http2Options? options = null)
    {
        _pipe = pipe;
        _options = options ?? new Http2Options();
        _decoder = new HpackDecoder();
    }

    /// <summary>Convenience for cleartext h2c: wraps the connection in its own duplex pipe.</summary>
    public Http2Connection(TcpConnection connection, Http2Options? options = null)
        : this(new TcpConnectionDualPipe(connection), options)
    {
    }

    /// <summary>True once the connection can serve no more.</summary>
    public bool IsBroken => _failed || _disposed;

    /// <summary>Stop accepting new streams.</summary>
    public void Shutdown() => _failed = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (PendingRequest pending in _streams.Values)
        {
            pending.Dispose();
        }
        _streams.Clear();

        foreach (PendingRequest pending in _ready)
        {
            pending.Dispose();
        }
        _ready.Clear();

        if (_inbound.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_inbound);
            _inbound = [];
        }
    }

    /// <summary>Serve until the peer goes away, answering each request with <paramref name="handler"/>.</summary>
    public Task RunBufferedAsync(Func<Http2Request, Http2Response> handler)
        => RunBufferedAsync(request => new ValueTask<Http2Response>(handler(request)));

    /// <summary>Serve until the peer goes away, with an asynchronous handler.</summary>
    public async Task RunBufferedAsync(Func<Http2Request, ValueTask<Http2Response>> handler)
    {
        try
        {
            // Our SETTINGS go out first; a peer that opened with the preface and a request is
            // already waiting on them.
            WriteSettings();
            await FlushAsync();

            while (!IsBroken)
            {
                ReadResult read = await _pipe.Input.ReadAsync();

                bool received = Accumulate(read.Buffer);
                _pipe.Input.AdvanceTo(read.Buffer.End);

                if (received)
                {
                    // A streamed writer that finishes inside this window needs no write of its own:
                    // the flush below carries it out together with every other response the pass
                    // produced. That coalescing is the whole reason buffered h2 is fast, and there
                    // is no reason a streamed response cannot share it.
                    _passFlushPending = true;
                    ParseAvailable();
                    await DispatchReadyAsync(handler);
                    _passFlushPending = false;
                    await FlushAsync();
                }

                if (read.IsCompleted || read.IsCanceled)
                {
                    return;
                }
            }
        }
        catch (Exception)
        {
            // A malformed peer is not a server fault. Nothing here is recoverable - HPACK in
            // particular has no resync point once the tables diverge.
            _failed = true;
        }
        finally
        {
            // A writer parked on flow-control credit will never be woken by a dead connection.
            ReleaseAllCreditWaiters();
            Dispose();
        }
    }

    // Copy into the accumulator, because a frame can straddle segments AND reads, and the parser
    // wants one contiguous view. The pipe's memory is only valid until AdvanceTo.
    private bool Accumulate(in ReadOnlySequence<byte> buffer)
    {
        bool any = false;

        foreach (ReadOnlyMemory<byte> segment in buffer)
        {
            if (segment.Length > 0)
            {
                Append(segment.Span);
                any = true;
            }
        }

        return any;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_inbound.Length - _inboundUsed < data.Length)
        {
            long size = Math.Max(16 * 1024, (long)_inbound.Length * 2);
            while (size < (long)_inboundUsed + data.Length)
            {
                size *= 2;
            }

            byte[] grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(size, Array.MaxLength));
            _inbound.AsSpan(0, _inboundUsed).CopyTo(grown);
            if (_inbound.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_inbound);
            }
            _inbound = grown;
        }

        data.CopyTo(_inbound.AsSpan(_inboundUsed));
        _inboundUsed += data.Length;
    }

    private async ValueTask DispatchReadyAsync(Func<Http2Request, ValueTask<Http2Response>> handler)
    {
        if (_ready.Count == 0)
        {
            return;
        }

        PendingRequest[] ready = _ready.ToArray();
        _ready.Clear();

        foreach (PendingRequest pending in ready)
        {
            ValueTask<Http2Response> inFlight;

            try
            {
                Http2Request request = pending.Freeze();

                if (TryDispatchStreamed(request, pending))
                {
                    continue;   // the writer owns this stream, and retires it when done
                }

                inFlight = handler(request);
            }
            catch
            {
                pending.Dispose();
                throw;
            }

            // Answered synchronously, which nearly every handler does. Stay inline: this response
            // is staged in time for the pass flush, so it still leaves with every other one, and
            // there is no Task to allocate.
            if (inFlight.IsCompletedSuccessfully)
            {
                try
                {
                    WriteResponse(pending.StreamId, inFlight.Result);
                }
                finally
                {
                    pending.Dispose();
                }
                continue;
            }

            // It parked - a database, an upstream, a disk. Awaiting here would hold every OTHER
            // stream on this connection behind it, including responses already staged and ready to
            // go, because they all share this one dispatch loop and one TCP connection. So it
            // finishes on its own and writes its own bytes when it does.
            _ = CompleteBufferedAsync(inFlight, pending);
        }
    }

    /// <summary>
    /// The tail of a handler that parked. Nothing is awaiting this, so everything the dispatch loop
    /// would have done afterwards has to happen here instead - retiring the request, and writing,
    /// since the pass flush has long gone by. Forgetting that tail in the streamed path is what
    /// leaked 20 GB.
    /// </summary>
    private async Task CompleteBufferedAsync(ValueTask<Http2Response> inFlight, PendingRequest pending)
    {
        try
        {
            Http2Response response = await inFlight;
            WriteResponse(pending.StreamId, response);
        }
        catch (Exception exception)
        {
            // Nobody can observe this task, so an escaping exception would vanish silently and the
            // peer would wait on a stream that is never coming.
            Console.Error.WriteLine($"[ioxide.http2] request handler faulted: {exception.GetBaseException().Message}");

            if (!IsBroken)
            {
                WriteResponse(pending.StreamId, new Http2Response { Status = 500 });
            }
        }
        finally
        {
            pending.Dispose();
            await MaybeFlushAsync();
        }
    }
}
