using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using ioxide.nghttp3;
using ioxide.ngtcp2;

namespace ioxide.httpclient3;

/// <summary>
/// One HTTP/3 connection to an origin: a QUIC connection opened on the reactor's ring plus a
/// client-side nghttp3 engine over it. The mirror of the server's Nghttp3Connection - same shim,
/// same callbacks, opposite direction.
///
/// Requests multiplex: each one takes its own QUIC stream, several can be in flight at once, and
/// each caller's await completes when that stream's response ends. A background pump loop feeds
/// inbound stream data into nghttp3 and completes the waiters; everything runs on the owning
/// reactor thread and resumes inline.
/// </summary>
public sealed class Http3ClientConnection : IDisposable
{
    private readonly QuicEngineConnection _quic;
    private readonly byte[] _authority;
    private readonly byte[] _egress = new byte[16 * 1024];

    private nint _nghttp3Handle;
    private GCHandle _self;
    private bool _isReady;     // uni streams bound, ready to submit
    private bool _failed;

    // Submissions made while the pump loop is processing a batch don't pump immediately: the loop
    // drains once at the end, so every request that a batch of completions produced leaves in ONE
    // sendmsg instead of one per request. Without this, each inline-resumed caller submits and
    // syscalls on its own.
    private bool _inPumpPass;
    private bool _pumpPending;

    // Requests whose response completed during the current ih3_read_stream call. Callbacks only
    // RECORD them; the waiters are resumed after the native call unwinds - resuming inside the
    // callback would let the caller's next request re-enter nghttp3 while nghttp3_conn_read_stream
    // is still on the stack (the same rule the server side follows).
    private readonly List<(PendingRequest Pending, Http3ClientResponse Response)> _completedThisPass = [];
    private bool _disposed;

    private readonly Dictionary<long, PendingRequest> _pending = new();

    // Completed once the h3 layer is usable (handshake done, uni streams bound). Requests issued
    // before that wait on it rather than failing - a fresh pool is the normal case.
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Http3ClientConnection(QuicEngineConnection quic, string authority)
    {
        _quic = quic;
        _authority = System.Text.Encoding.ASCII.GetBytes(authority);

        // Nothing else wakes a client connection until the peer sends stream data, and the peer
        // has nothing to send until we ask - so the handshake signal is what starts h3 setup.
        _quic.HandshakeCompleted = () => TrySetup();

        _ = PumpLoopAsync();
    }

    /// <summary>True once the connection can't serve more requests (handshake failed, peer gone,
    /// protocol error). The pool drops these.</summary>
    public bool IsBroken => _failed || _disposed;

    /// <summary>In-flight requests on this connection.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Send one request on its own stream and await the response. The response owns its bytes -
    /// dispose it when done.
    /// </summary>
    public ValueTask<Http3ClientResponse> SendAsync(Http3ClientRequest request)
    {
        if (IsBroken)
        {
            throw new Http3ClientException("connection is broken");
        }

        // Fast path: an established connection submits with no extra state machine.
        return _isReady ? SendCore(request) : SendWhenReadyAsync(request);
    }

    private async ValueTask<Http3ClientResponse> SendWhenReadyAsync(Http3ClientRequest request)
    {
        await _ready.Task;   // handshake + h3 setup; faulted if the connection died first
        return await SendCore(request);
    }

    private unsafe ValueTask<Http3ClientResponse> SendCore(Http3ClientRequest request)
    {
        if (IsBroken || !_isReady)
        {
            throw new Http3ClientException("connection is not usable");
        }

        long streamId = _quic.OpenBidiStream();
        if (streamId < 0)
        {
            throw new Http3ClientException("no stream available (peer's concurrency limit reached)");
        }

        byte[] headers = PackRequestHeaders(request, out int headersLength);
        int submitResult;
        fixed (byte* headersPointer = headers)
        fixed (byte* bodyPointer = request.Body.Span)
        {
            submitResult = Nghttp3.ih3_submit_request(_nghttp3Handle, streamId,
                headersPointer, (nuint)headersLength, bodyPointer, (nuint)request.Body.Length);
        }
        ArrayPool<byte>.Shared.Return(headers);

        if (submitResult != 0)
        {
            throw new Http3ClientException($"submit_request failed: {Nghttp3.StrError(submitResult)}");
        }

        var pending = new PendingRequest();
        _pending[streamId] = pending;

        if (_inPumpPass)
        {
            _pumpPending = true;   // the loop's own drain will carry it, batched with its siblings
        }
        else
        {
            PumpEgress();          // issued from outside a pass: send it now
        }
        return pending.Task;
    }

    // --- the pump: inbound stream data into nghttp3, completions out to the waiters -------------

    private async Task PumpLoopAsync()
    {
        try
        {
            while (true)
            {
                QuicRecvSnapshot snapshot = await _quic.ReadAsync();

                // Setup is lazy: uni streams only open once the handshake completes, and the first
                // wake is proof that it did.
                if (!_isReady && !_failed)
                {
                    TrySetup();   // belt and braces: the handshake signal normally gets here first
                }

                _inPumpPass = true;
                try
                {
                    while (_quic.TryGetDelivery(in snapshot, out QuicRecvRing.Delivery delivery))
                    {
                        if (!_failed)
                        {
                            Feed(in delivery);   // callbacks only record completions
                        }
                        _quic.ReturnBuffer(in delivery);
                    }

                    // nghttp3 has unwound: safe to resume the waiters. Their follow-up requests
                    // submit while _inPumpPass is still set, so they all ride one batched drain.
                    CompleteFinishedRequests();
                }
                finally
                {
                    _inPumpPass = false;
                }

                if (!_failed)
                {
                    _pumpPending = false;
                    PumpEgress();
                }

                if (snapshot.IsClosed || _failed)
                {
                    break;
                }
                _quic.ResetRead();
            }
        }
        catch (Exception e)
        {
            FailAll(new Http3ClientException($"connection pump failed: {e.GetBaseException().Message}"));
        }
        finally
        {
            FailAll(new Http3ClientException("connection closed"));
            _quic.DecRef();
            Dispose();
        }
    }

    private unsafe void Feed(in QuicRecvRing.Delivery delivery)
    {
        if (_nghttp3Handle == 0 || _failed)
        {
            return;   // torn down by an inline resume earlier in this pass
        }

        if (delivery.Kind != QuicStreamEvent.Data)
        {
            // Mirror the QUIC-level teardown into nghttp3 so it frees the stream's state (and the
            // shim frees a copied request body); without this a long-lived connection accretes
            // stream objects for every request it ever made.
            Nghttp3.ih3_close_stream(_nghttp3Handle, delivery.StreamId, delivery.AppError);

            if (_pending.Remove(delivery.StreamId, out PendingRequest? aborted))
            {
                aborted.Fail(new Http3ClientException($"stream {delivery.Kind.ToString().ToLowerInvariant()}"));
            }
            return;
        }

        ReadOnlySpan<byte> payload = delivery.AsSpan();
        long readResult;
        fixed (byte* payloadPointer = payload)
        {
            readResult = Nghttp3.ih3_read_stream(_nghttp3Handle, delivery.StreamId,
                payloadPointer, (nuint)payload.Length, delivery.Fin ? 1 : 0);
        }

        if (readResult < 0)
        {
            _failed = true;
            FailAll(new Http3ClientException($"read_stream failed: {Nghttp3.StrError((int)readResult)}"));
        }
    }

    // Every datagram this drain produces rides one batched sendmsg (see SendBatched) - without
    // the wrapper each request would cost its own syscall.
    private void PumpEgress()
    {
        if (_nghttp3Handle == 0)
        {
            return;
        }
        _quic.SendBatched(PumpEgressCore);
    }

    private unsafe void PumpEgressCore()
    {
        fixed (byte* egressPointer = _egress)
        {
            while (true)
            {
                if (_nghttp3Handle == 0 || _failed)
                {
                    return;   // an inline teardown (send failure) freed the engine mid-drain
                }

                long streamId;
                int fin;
                long produced = Nghttp3.ih3_writev(_nghttp3Handle, &streamId, &fin,
                    egressPointer, (nuint)_egress.Length);
                if (produced < 0)
                {
                    _failed = true;
                    return;
                }
                if (produced == 0 && streamId == -1)
                {
                    return;
                }
                _quic.SendStream(streamId, _egress.AsSpan(0, (int)produced), fin != 0);
            }
        }
    }

    // Open our three uni streams and stand up nghttp3. Fails silently before the handshake (the
    // peer's stream allowance isn't known yet) and is retried on the next wake.
    private unsafe bool TrySetup()
    {
        if (_isReady || _failed)
        {
            return _isReady;   // both the handshake signal and the loop's retry can land here
        }

        long control = _quic.OpenUniStream();
        long encoder = _quic.OpenUniStream();
        long decoder = _quic.OpenUniStream();
        if (control < 0 || encoder < 0 || decoder < 0)
        {
            return false;
        }

        _self = GCHandle.Alloc(this);
        var callbacks = new Nghttp3.Callbacks
        {
            OnBeginHeaders = &CallbackBeginHeaders,
            OnHeader       = &CallbackHeader,
            OnEndHeaders   = &CallbackEndHeaders,
            OnData         = &CallbackData,
            OnEndStream    = &CallbackEndStream,
        };

        _nghttp3Handle = Nghttp3.ih3_client_new(callbacks, (void*)GCHandle.ToIntPtr(_self));
        if (_nghttp3Handle == 0)
        {
            _failed = true;
            _ready.TrySetException(new Http3ClientException("nghttp3 client init failed"));
            return false;
        }

        int bindResult = Nghttp3.ih3_bind_streams(_nghttp3Handle, control, encoder, decoder);
        if (bindResult != 0)
        {
            _failed = true;
            _ready.TrySetException(new Http3ClientException($"bind streams failed: {Nghttp3.StrError(bindResult)}"));
            return false;
        }

        _isReady = true;
        PumpEgress();   // SETTINGS preface
        _ready.TrySetResult(true);
        return true;
    }

    // [u16 namelen][name][u16 valuelen][value]... - the shim's packed field format. Pseudo-headers
    // first, in the order h3 requires.
    private unsafe byte[] PackRequestHeaders(Http3ClientRequest request, out int written)
    {
        int capacity = 64 + request.Method.Length + request.Path.Length + _authority.Length + 32;
        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in request.Headers.AsSpan())
        {
            capacity += 4 + field.Key.Length + field.Value.Length;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        int cursor = 0;

        Add(buffer, ref cursor, ":method"u8, request.Method.Span);
        Add(buffer, ref cursor, ":scheme"u8, "https"u8);
        Add(buffer, ref cursor, ":authority"u8, _authority);
        Add(buffer, ref cursor, ":path"u8, request.Path.Span);

        if (request.Body.Length > 0)
        {
            Span<byte> number = stackalloc byte[20];
            Utf8Formatter.TryFormat(request.Body.Length, number, out int numberLength);
            Add(buffer, ref cursor, "content-length"u8, number[..numberLength]);
        }

        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in request.Headers.AsSpan())
        {
            Add(buffer, ref cursor, field.Key.Span, field.Value.Span);
        }

        written = cursor;
        return buffer;

        static void Add(byte[] destination, ref int cursor, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            destination[cursor++] = (byte)name.Length;
            destination[cursor++] = (byte)(name.Length >> 8);
            name.CopyTo(destination.AsSpan(cursor));
            cursor += name.Length;

            destination[cursor++] = (byte)value.Length;
            destination[cursor++] = (byte)(value.Length >> 8);
            value.CopyTo(destination.AsSpan(cursor));
            cursor += value.Length;
        }
    }

    // Resume every caller whose response finished during this pass. Runs OUTSIDE the native call.
    private void CompleteFinishedRequests()
    {
        if (_completedThisPass.Count == 0)
        {
            return;
        }

        // Snapshot-and-clear first: a resumed caller may submit again and land new completions
        // here, and the list must not be mutated while it is being walked.
        var finished = _completedThisPass.ToArray();
        _completedThisPass.Clear();

        foreach ((PendingRequest pending, Http3ClientResponse response) in finished)
        {
            pending.Complete(response);
        }
    }

    private void FailAll(Exception error)
    {
        _ready.TrySetException(error);   // unblock anyone waiting for a connection that died
        foreach (PendingRequest pending in _pending.Values)
        {
            pending.Fail(error);
        }
        _pending.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _failed = true;   // the pump's guards key off this; the handle is about to go

        if (_nghttp3Handle != 0)
        {
            Nghttp3.ih3_free(_nghttp3Handle);
            _nghttp3Handle = 0;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    // --- unmanaged callbacks (reactor thread, inside ih3_read_stream) ---------------------------

    private static unsafe Http3ClientConnection From(void* user)
        => (Http3ClientConnection)GCHandle.FromIntPtr((nint)user).Target!;

    [UnmanagedCallersOnly]
    private static unsafe void CallbackBeginHeaders(void* user, long streamId)
    {
        Http3ClientConnection connection = From(user);
        if (connection._pending.TryGetValue(streamId, out PendingRequest? pending))
        {
            pending.Response ??= new Http3ClientResponse();
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackHeader(void* user, long streamId, byte* name, nuint nameLength,
        byte* value, nuint valueLength)
    {
        Http3ClientConnection connection = From(user);
        if (!connection._pending.TryGetValue(streamId, out PendingRequest? pending) ||
            pending.Response is not { } response)
        {
            return;
        }

        var fieldName = new ReadOnlySpan<byte>(name, (int)nameLength);
        var fieldValue = new ReadOnlySpan<byte>(value, (int)valueLength);

        // :status is the response's own field, not a header line.
        if (fieldName.SequenceEqual(":status"u8))
        {
            if (Utf8Parser.TryParse(fieldValue, out int status, out _))
            {
                response.Status = status;
            }
            return;
        }
        if (fieldName.Length > 0 && fieldName[0] == (byte)':')
        {
            return;   // any other pseudo-header: not part of the field section
        }

        (int Offset, int Length) nameRange = response.Append(fieldName);
        (int Offset, int Length) valueRange = response.Append(fieldValue);
        response.AddHeaderRange(nameRange, valueRange);
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackEndHeaders(void* user, long streamId, int fin)
    {
        Http3ClientConnection connection = From(user);
        if (connection._pending.TryGetValue(streamId, out PendingRequest? pending) &&
            pending.Response is { } response && !pending.HeadersDone)
        {
            pending.HeadersDone = true;
            pending.BodyStart = response.ArenaLength;   // body bytes follow the header bytes
        }
        // A second field section is TRAILERS: leaving BodyStart alone keeps the body range valid
        // (moving it here would slice past the body into the trailer bytes).
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackData(void* user, long streamId, byte* data, nuint dataLength)
    {
        Http3ClientConnection connection = From(user);
        if (connection._pending.TryGetValue(streamId, out PendingRequest? pending) &&
            pending.Response is { } response)
        {
            response.Append(new ReadOnlySpan<byte>(data, (int)dataLength));
            pending.BodyLength += (int)dataLength;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void CallbackEndStream(void* user, long streamId)
    {
        Http3ClientConnection connection = From(user);
        if (!connection._pending.Remove(streamId, out PendingRequest? pending))
        {
            return;
        }

        Http3ClientResponse response = pending.Response ?? new Http3ClientResponse();
        response.SetBodyRange((pending.BodyStart, pending.BodyLength));
        response.Freeze();

        // Record only - the waiter is resumed by CompleteFinishedRequests once nghttp3 unwinds.
        connection._completedThisPass.Add((pending, response));

        // nghttp3 is done with this stream; let it release the stream's state.
        if (connection._nghttp3Handle != 0)
        {
            Nghttp3.ih3_close_stream(connection._nghttp3Handle, streamId, 0);
        }
    }

    /// <summary>
    /// One in-flight request: an inline-resuming completion source plus the response being
    /// assembled from the callbacks. Completions fire from inside ih3_read_stream, so the waiter
    /// resumes on the reactor thread - which is exactly where it awaited.
    /// </summary>
    private sealed class PendingRequest : IValueTaskSource<Http3ClientResponse>
    {
        private ManualResetValueTaskSourceCore<Http3ClientResponse> _core = new()
        {
            RunContinuationsAsynchronously = false,
        };

        public Http3ClientResponse? Response;
        public bool HeadersDone;   // first field section seen; a later one is trailers
        public int BodyStart;
        public int BodyLength;

        public ValueTask<Http3ClientResponse> Task => new(this, _core.Version);

        public void Complete(Http3ClientResponse response) => _core.SetResult(response);

        public void Fail(Exception error) => _core.SetException(error);

        Http3ClientResponse IValueTaskSource<Http3ClientResponse>.GetResult(short token) => _core.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<Http3ClientResponse>.GetStatus(short token) => _core.GetStatus(token);

        void IValueTaskSource<Http3ClientResponse>.OnCompleted(Action<object?> continuation, object? state,
            short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token,
                flags & ~ValueTaskSourceOnCompletedFlags.UseSchedulingContext);
    }
}
