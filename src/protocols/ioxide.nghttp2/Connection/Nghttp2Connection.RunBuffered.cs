using System.Buffers;
using System.IO.Pipelines;

namespace ioxide.nghttp2;

/// <summary>
/// The serving loop: read ciphertext-free bytes off the ring, feed them to nghttp2, dispatch
/// whatever requests completed, drain the egress. One pass per recv batch, so a batch carrying
/// several requests answers them all before a single send goes out.
/// </summary>
public sealed partial class Nghttp2Connection
{
    /// <summary>Serve until the peer goes away, answering each request with <paramref name="handler"/>.</summary>
    public Task RunBufferedAsync(Func<Nghttp2Request, Nghttp2Response> handler)
        => RunBufferedAsync(request => new ValueTask<Nghttp2Response>(handler(request)));

    /// <summary>Serve until the peer goes away, with an asynchronous handler.</summary>
    public async Task RunBufferedAsync(Func<Nghttp2Request, ValueTask<Nghttp2Response>> handler)
    {
        try
        {
            // SETTINGS go out before anything is read: a peer that opens with the preface and a
            // request immediately expects ours to be on the way.
            await FlushEgressAsync();

            while (!IsBroken)
            {
                ReadResult read = await _pipe.Input.ReadAsync();

                Feed(read.Buffer);
                _pipe.Input.AdvanceTo(read.Buffer.End);

                // Handlers run HERE, after ih2_read has unwound - see the callbacks file.
                await DispatchReadyAsync(handler);
                await FlushEgressAsync();

                if (read.IsCompleted || read.IsCanceled || Nghttp2.ih2_is_dead(_handle) != 0)
                {
                    return;
                }
            }
        }
        catch (Exception)
        {
            // A broken peer is not a server fault. The connection is torn down in the caller's
            // finally; nothing here is recoverable.
            _failed = true;
        }
        finally
        {
            Dispose();
        }
    }

    // Feed each segment straight to nghttp2. The segments are the ring's own memory when the pipe
    // is a TcpConnectionDualPipe, so nothing is copied; a frame split across two of them is fine,
    // because nghttp2 carries partial frames between calls.
    private unsafe void Feed(in ReadOnlySequence<byte> buffer)
    {
        foreach (ReadOnlyMemory<byte> segment in buffer)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            using System.Buffers.MemoryHandle handle = segment.Pin();
            nint consumed = Nghttp2.ih2_read(_handle, (byte*)handle.Pointer, (nuint)segment.Length);
            if (consumed < 0)
            {
                _failed = true;   // protocol error; the drain still sends GOAWAY
                return;
            }
        }
    }

    private async ValueTask DispatchReadyAsync(Func<Nghttp2Request, ValueTask<Nghttp2Response>> handler)
    {
        if (_readyThisPass.Count == 0)
        {
            return;
        }

        // Snapshot and clear first: a handler that awaits can let another read pass land more
        // requests here, and the list must not be mutated while it is walked.
        PendingRequest[] ready = _readyThisPass.ToArray();
        _readyThisPass.Clear();

        foreach (PendingRequest pending in ready)
        {
            try
            {
                Nghttp2Request request = pending.Freeze();
                Nghttp2Response response = await handler(request);
                SubmitResponse(pending.StreamId, response);
            }
            finally
            {
                // The arena backs the request's memories, so it can only go back once the handler
                // has returned and the response is submitted (which copies).
                pending.Dispose();
            }
        }
    }
}
