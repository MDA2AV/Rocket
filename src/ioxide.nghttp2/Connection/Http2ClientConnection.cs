using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using ioxide.httpclient;

namespace ioxide.nghttp2;

/// <summary>
/// One HTTP/2 connection over a ring socket: many requests in flight at once, multiplexed by
/// nghttp2 onto a single TCP byte stream.
///
/// nghttp2 is sans-I/O, so the split is clean - it owns framing, HPACK and flow control, and this
/// class owns the socket, the loop and the waiters. A pump task reads bytes, feeds them to
/// <c>ih2_read</c>, and drains whatever that produced back to the socket.
///
/// This is <b>h2c</b> - cleartext HTTP/2 with prior knowledge (RFC 9113 §3.3). There is no TLS and
/// therefore no ALPN, so the peer must already be expecting HTTP/2 on this port; there is no
/// Upgrade dance and no h2-over-TLS here.
/// </summary>
/// <remarks>
/// Reactor thread only. Response completions are recorded by the native callbacks and resumed
/// AFTER <c>ih2_read</c> returns, so a caller that immediately submits another request can never
/// re-enter nghttp2 while it is still on the stack.
/// </remarks>
public sealed class Http2ClientConnection : IDisposable
{
    // nghttp2's largest frame is 16 MiB, but it only emits SETTINGS_MAX_FRAME_SIZE-sized DATA
    // frames (16 KiB by default) plus a 9-byte header. 64 KiB leaves ample room for a drain pass.
    private const int EgressBufferSize = 64 * 1024;
    private const int IngressBufferSize = 64 * 1024;

    private readonly RingSocket _socket;
    private readonly byte[] _authority;
    private readonly byte[] _egress = new byte[EgressBufferSize];

    private nint _nghttp2Handle;
    private GCHandle _self;
    private bool _failed;
    private bool _disposed;

    private readonly Dictionary<int, PendingRequest> _pending = new();

    // Completions and stream failures recorded during the current ih2_read/ih2_write call,
    // resumed once it unwinds. Failures ride the same list because a resumed waiter can retry
    // through the pool - and the pool may dispose this connection, which must never happen while
    // nghttp2 is still on the stack.
    private readonly List<(PendingRequest Pending, HttpClientResponse? Response, Exception? Error)> _completedThisPass = [];

    // Set while the pump owns the drain, so a request submitted by a resumed caller rides the
    // pump's own egress pass instead of issuing its own.
    private bool _inPumpPass;

    // One drain at a time. Both the pump and SendAsync can start one, and a drain awaits socket
    // sends - so without this two of them interleave writes out of the single _egress buffer and
    // put a corrupt frame stream on the wire (the peer then closes on a protocol error).
    private bool _draining;
    private bool _drainAgain;

    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Http2ClientConnection(RingSocket socket, string authority)
    {
        _socket = socket;
        _authority = System.Text.Encoding.ASCII.GetBytes(authority);
    }

    public bool IsBroken => _failed || _disposed || _retiring;

    // Set when the peer refuses a stream (GOAWAY / max-requests reached). The connection can still
    // finish what is in flight, but the pool must stop handing it new requests and open a
    // replacement.
    private bool _retiring;

    public int InFlight => _pending.Count;

    public static async Task<Http2ClientConnection> ConnectAsync(IRingHost host, string ip, ushort port,
        string authority)
    {
        RingSocket socket = RingSocket.CreateTcp(host);
        try
        {
            int result = await socket.ConnectAsync(ip, port);
            if (result < 0)
            {
                throw new Http2ClientException($"connect to {ip}:{port} failed: errno {-result}");
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var connection = new Http2ClientConnection(socket, authority);
        connection.Setup();
        _ = connection.PumpLoopAsync();
        await connection._ready.Task;   // preface + SETTINGS on the wire before the first request
        return connection;
    }

    private unsafe void Setup()
    {
        _self = GCHandle.Alloc(this);
        var callbacks = new Nghttp2.Callbacks
        {
            OnBeginHeaders = &CallbackBeginHeaders,
            OnHeader       = &CallbackHeader,
            OnEndHeaders   = &CallbackEndHeaders,
            OnData         = &CallbackData,
            OnEndStream    = &CallbackEndStream,
            OnStreamError  = &CallbackStreamError,
        };

        _nghttp2Handle = Nghttp2.ih2_client_new(callbacks, (void*)GCHandle.ToIntPtr(_self));
        if (_nghttp2Handle == 0)
        {
            _failed = true;
            _ready.TrySetException(new Http2ClientException("nghttp2 client init failed"));
        }
    }

    // --- requests -------------------------------------------------------------------------------

    public unsafe ValueTask<HttpClientResponse> SendAsync(HttpClientRequest request)
    {
        if (IsBroken)
        {
            // Nothing was submitted, so resending elsewhere is unconditionally safe - surfaced as
            // the retryable kind so the pool moves the request to a replacement connection.
            throw new Http2StreamRefusedException("connection is retiring or closed; request was not submitted");
        }

        byte[] headers = PackRequestHeaders(request, out int headersLength);
        int streamId;
        fixed (byte* headersPointer = headers)
        fixed (byte* bodyPointer = request.Body.Span)
        {
            streamId = Nghttp2.ih2_submit_request(_nghttp2Handle, headersPointer, (nuint)headersLength,
                bodyPointer, (nuint)request.Body.Length);
        }
        ArrayPool<byte>.Shared.Return(headers);

        if (streamId < 0)
        {
            throw new Http2ClientException($"submit_request failed: {Nghttp2.StrError(streamId)}");
        }

        var pending = new PendingRequest();
        _pending[streamId] = pending;

        if (!_inPumpPass)
        {
            _ = DrainDetachedAsync();   // issued from outside a pass: put it on the wire now
        }

        return pending.Task;
    }

    // The detached drain SendAsync fires. Swallowing its failure would strand every in-flight
    // waiter until the pump happens to notice (_failed is set, but the pump sits in recv): fail
    // them now and close the socket, which errors that recv out and lets the pump exit.
    private async Task DrainDetachedAsync()
    {
        try
        {
            await PumpEgressAsync();
        }
        catch (Exception e)
        {
            FailAll(new Http2ClientException($"egress drain failed: {e.GetBaseException().Message}"));
            Dispose();
        }
    }

    // [u16 namelen][name][u16 valuelen][value]... - pseudo-headers first, in the order h2 requires.
    private byte[] PackRequestHeaders(HttpClientRequest request, out int written)
    {
        int capacity = 64 + request.Method.Length + request.Path.Length + _authority.Length + 32;
        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in request.Headers.AsSpan())
        {
            capacity += 4 + field.Key.Length + field.Value.Length;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        int cursor = 0;

        static void Write(byte[] destination, ref int offset, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            destination[offset++] = (byte)name.Length;
            destination[offset++] = (byte)(name.Length >> 8);
            name.CopyTo(destination.AsSpan(offset));
            offset += name.Length;

            destination[offset++] = (byte)value.Length;
            destination[offset++] = (byte)(value.Length >> 8);
            value.CopyTo(destination.AsSpan(offset));
            offset += value.Length;
        }

        Write(buffer, ref cursor, ":method"u8, request.Method.Span);
        Write(buffer, ref cursor, ":scheme"u8, "http"u8);
        Write(buffer, ref cursor, ":authority"u8, _authority);
        Write(buffer, ref cursor, ":path"u8, request.Path.Span);

        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in request.Headers.AsSpan())
        {
            Write(buffer, ref cursor, field.Key.Span, field.Value.Span);
        }

        written = cursor;
        return buffer;
    }

    // --- the pump -------------------------------------------------------------------------------

    private async Task PumpLoopAsync()
    {
        nint receive = Marshal.AllocHGlobal(IngressBufferSize);
        try
        {
            await PumpEgressAsync();      // connection preface + SETTINGS
            _ready.TrySetResult(true);

            while (!_failed && !_disposed)
            {
                int n = await _socket.RecvAsync(receive, IngressBufferSize);
                if (n <= 0)
                {
                    throw new Http2ClientException(n == 0 ? "peer closed the connection" : $"recv failed: errno {-n}");
                }

                _inPumpPass = true;
                try
                {
                    nint consumed = Feed(receive, n);
                    if (consumed < 0)
                    {
                        throw new Http2ClientException($"read failed: {Nghttp2.StrError((int)consumed)}");
                    }

                    // nghttp2 has unwound: safe to resume the waiters. Their follow-up requests
                    // submit while _inPumpPass is set, so they all ride the drain below.
                    CompleteFinishedRequests();
                }
                finally
                {
                    _inPumpPass = false;
                }

                await PumpEgressAsync();   // ACKs, WINDOW_UPDATEs and any newly submitted requests

                if (_nghttp2Handle != 0 && Nghttp2.ih2_is_dead(_nghttp2Handle) != 0)
                {
                    _retiring = true;   // GOAWAY drained; the pool opens a replacement
                    break;
                }
            }
        }
        catch (Exception e)
        {
            FailAll(new Http2ClientException($"connection pump failed: {e.GetBaseException().Message}"));
        }
        finally
        {
            Marshal.FreeHGlobal(receive);

            // A clean retirement (GOAWAY drained, nothing left to read) leaves only streams the
            // peer never processed: ones past last_stream_id or never sent. RFC 9113 8.7 makes
            // resending those safe, so they fail retryable and the pool resends them. Anything
            // else is a real connection failure.
            FailAll(_retiring && !_failed
                ? new Http2StreamRefusedException("connection retired (GOAWAY); request was not processed")
                : new Http2ClientException("connection closed"));
            Dispose();
        }
    }

    private async ValueTask PumpEgressAsync()
    {
        if (_draining)
        {
            _drainAgain = true;   // a drain is mid-flight; it will pick up what we just queued
            return;
        }

        _draining = true;
        try
        {
            await DrainLoopAsync();

            while (_drainAgain)
            {
                _drainAgain = false;
                await DrainLoopAsync();
            }
        }
        finally
        {
            _draining = false;

            // Stream failures can be deposited DURING a drain: after a GOAWAY, nghttp2 refuses
            // queued streams inside mem_send. Resolve them here, outside the native call - on the
            // error path too, or they would be lost (their pendings left _pending already).
            CompleteFinishedRequests();
        }
    }

    private async ValueTask DrainLoopAsync()
    {
        while (true)
        {
            if (_nghttp2Handle == 0 || _disposed)
            {
                return;
            }

            nint produced = DrainEgress();

            if (produced < 0)
            {
                _failed = true;
                throw new Http2ClientException($"write failed: {Nghttp2.StrError((int)produced)}");
            }
            if (produced == 0)
            {
                return;
            }

            await SendAllAsync(_egress, (int)produced);
        }
    }

    private async ValueTask SendAllAsync(byte[] buffer, int length)
    {
        using MemoryHandle pinned = buffer.AsMemory().Pin();
        nint basePointer = PointerOf(pinned);

        int sent = 0;
        while (sent < length)
        {
            int n = await _socket.SendAsync(basePointer + sent, length - sent);
            if (n <= 0)
            {
                _failed = true;
                throw new Http2ClientException(n == 0 ? "peer closed while sending" : $"send failed: errno {-n}");
            }
            sent += n;
        }
    }

    // Pointer-touching helpers, kept out of the async methods above (no await may appear in an
    // unsafe context).
    private unsafe nint Feed(nint receive, int length)
        => Nghttp2.ih2_read(_nghttp2Handle, (byte*)receive, (nuint)length);

    private unsafe nint DrainEgress()
    {
        fixed (byte* egressPointer = _egress)
        {
            return Nghttp2.ih2_write(_nghttp2Handle, egressPointer, (nuint)_egress.Length);
        }
    }

    private static unsafe nint PointerOf(MemoryHandle pinned) => (nint)pinned.Pointer;

    // Runs OUTSIDE ih2_read/ih2_write, so a resumed caller may safely submit again - or retry
    // through the pool, which may dispose this very connection.
    private void CompleteFinishedRequests()
    {
        if (_completedThisPass.Count == 0)
        {
            return;
        }

        // Snapshot-and-clear first: a resumed caller can land new completions here, and the list
        // must not be mutated while it is being walked.
        var finished = _completedThisPass.ToArray();
        _completedThisPass.Clear();

        foreach ((PendingRequest pending, HttpClientResponse? response, Exception? error) in finished)
        {
            if (error is null)
            {
                pending.Complete(response!);
            }
            else
            {
                pending.Fail(error);
            }
        }
    }

    private void FailAll(Exception error)
    {
        // Broken before anything resumes: an inline-resumed caller that immediately retries must
        // see IsBroken and go to the pool, not submit onto this connection.
        _failed = true;
        _ready.TrySetException(error);

        // Deposited but not yet flushed: those pendings already left _pending, so they would be
        // missed below. A recorded response is a real completed exchange - deliver it.
        CompleteFinishedRequests();

        if (_pending.Count == 0)
        {
            return;
        }

        // Snapshot-and-clear: resumes run inline and may re-enter (a retry submitting elsewhere
        // still touches pool state), and _pending must not change under the enumeration.
        PendingRequest[] pendings = [.. _pending.Values];
        _pending.Clear();
        foreach (PendingRequest pending in pendings)
        {
            pending.Fail(error);
        }
    }

    // --- native callbacks: deposit only ---------------------------------------------------------

    private static unsafe Http2ClientConnection FromUser(void* user)
        => (Http2ClientConnection)GCHandle.FromIntPtr((nint)user).Target!;

    [UnmanagedCallersOnly]
    private static unsafe void CallbackBeginHeaders(void* user, int streamId)
    {
        Http2ClientConnection connection = FromUser(user);
        if (connection._pending.TryGetValue(streamId, out PendingRequest? pending))
        {
            pending.Response = new HttpClientResponse();
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackHeader(void* user, int streamId, byte* name, nuint nameLen,
        byte* value, nuint valueLen)
    {
        Http2ClientConnection connection = FromUser(user);
        if (!connection._pending.TryGetValue(streamId, out PendingRequest? pending) ||
            pending.Response is not { } response)
        {
            return;
        }

        var nameSpan = new ReadOnlySpan<byte>(name, (int)nameLen);
        var valueSpan = new ReadOnlySpan<byte>(value, (int)valueLen);

        // ":status" is the status line's stand-in; everything else is an ordinary field.
        if (nameSpan.SequenceEqual(":status"u8))
        {
            int status = 0;
            foreach (byte digit in valueSpan)
            {
                status = (status * 10) + (digit - (byte)'0');
            }
            response.Status = status;
            return;
        }

        (int Offset, int Length) nameRange = response.Append(nameSpan);
        (int Offset, int Length) valueRange = response.Append(valueSpan);
        response.AddHeaderRange(nameRange, valueRange);
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackEndHeaders(void* user, int streamId)
    {
        Http2ClientConnection connection = FromUser(user);
        if (connection._pending.TryGetValue(streamId, out PendingRequest? pending) &&
            pending.Response is { } response && !pending.HeadersDone)
        {
            pending.HeadersDone = true;
            pending.BodyStart = response.ArenaLength;   // body bytes follow the header bytes
        }
        // A second field section is TRAILERS: leaving BodyStart alone keeps the body range valid.
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackData(void* user, int streamId, byte* data, nuint dataLen)
    {
        Http2ClientConnection connection = FromUser(user);
        if (connection._pending.TryGetValue(streamId, out PendingRequest? pending) &&
            pending.Response is { } response)
        {
            response.Append(new ReadOnlySpan<byte>(data, (int)dataLen));
            pending.BodyLength += (int)dataLen;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackEndStream(void* user, int streamId)
    {
        Http2ClientConnection connection = FromUser(user);
        if (!connection._pending.Remove(streamId, out PendingRequest? pending))
        {
            return;
        }

        HttpClientResponse response = pending.Response ?? new HttpClientResponse();
        response.SetBodyRange((pending.BodyStart, pending.BodyLength));
        response.Freeze();

        // Record only - resumed by CompleteFinishedRequests once nghttp2 unwinds.
        connection._completedThisPass.Add((pending, response, null));
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackStreamError(void* user, int streamId, uint errorCode)
    {
        Http2ClientConnection connection = FromUser(user);

        // REFUSED_STREAM means the peer never processed the request - it is retiring the connection
        // (GOAWAY, or a max-requests limit like nginx's keepalive_requests). RFC 9113 8.7 makes
        // retrying it explicitly safe, so it is surfaced as retryable and the connection retires.
        bool refused = errorCode == RefusedStream;
        if (refused)
        {
            connection._retiring = true;
        }

        if (connection._pending.Remove(streamId, out PendingRequest? pending))
        {
            // Record only, exactly like EndStream. Failing here would resume the waiter INSIDE
            // ih2_read/ih2_write (this callback fires during both - GOAWAY closes live streams in
            // mem_recv and refuses queued ones in mem_send), and a resumed caller retries through
            // the pool, which prunes retiring connections - Dispose would then run ih2_free while
            // nghttp2 is still on this very stack.
            connection._completedThisPass.Add((pending, null, refused
                ? new Http2StreamRefusedException($"stream {streamId} refused; connection is retiring")
                : new Http2ClientException($"stream {streamId} reset (error {errorCode})")));
        }
    }

    /// <summary>HTTP/2 REFUSED_STREAM (RFC 9113 section 7).</summary>
    private const uint RefusedStream = 7;

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _failed = true;

        if (_nghttp2Handle != 0)
        {
            Nghttp2.ih2_free(_nghttp2Handle);
            _nghttp2Handle = 0;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
        _socket.Dispose();
    }

    /// <summary>One in-flight request. IValueTaskSource with asynchronous continuations OFF, so the
    /// caller resumes inline on the reactor thread.</summary>
    private sealed class PendingRequest : IValueTaskSource<HttpClientResponse>
    {
        private ManualResetValueTaskSourceCore<HttpClientResponse> _core = new()
        {
            RunContinuationsAsynchronously = false,
        };

        public HttpClientResponse? Response;
        public bool HeadersDone;
        public int BodyStart;
        public int BodyLength;

        public ValueTask<HttpClientResponse> Task => new(this, _core.Version);

        public void Complete(HttpClientResponse response) => _core.SetResult(response);

        public void Fail(Exception error) => _core.SetException(error);

        public HttpClientResponse GetResult(short token) => _core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags & ~ValueTaskSourceOnCompletedFlags.UseSchedulingContext);
    }
}

/// <summary>Any HTTP/2 client failure: connect, session, stream, or a protocol error.</summary>
public class Http2ClientException : Exception
{
    public Http2ClientException(string message) : base(message) { }
}

/// <summary>
/// The peer refused the stream (REFUSED_STREAM), meaning it never processed the request - so
/// resending it on a fresh connection is safe regardless of the method's idempotence.
/// </summary>
public sealed class Http2StreamRefusedException : Http2ClientException
{
    public Http2StreamRefusedException(string message) : base(message) { }
}
