using System.Buffers;

namespace ioxide.http2;

/// <summary>
/// The STREAMED-RESPONSE half: headers first, then the body as DATA frames pushed by the handler
/// through an <see cref="Http2ResponseWriter"/> as it produces them.
///
/// Owning the framing keeps this a push - a chunk is written the moment it exists rather than
/// when a library asks for it. What HTTP/2 adds over HTTP/3 is that credit is shared: every
/// stream rides one TCP connection and both a per-stream and a connection window must allow a
/// write, so a flush can block on either and is woken by a WINDOW_UPDATE for either.
/// </summary>
public sealed partial class Http2Connection
{
    private readonly Stack<Http2ResponseWriter> _writerPool = new();
    private readonly Dictionary<int, List<TaskCompletionSource>> _creditWaiters = new();
    private Func<Http2Request, Http2ResponseWriter, ValueTask>? _streamedHandler;

    /// <summary>
    /// Serve this connection with each response body produced through a writer rather than
    /// returned whole. The handler owns its stream until it completes the writer.
    /// </summary>
    public Task RunAsync(Func<Http2Request, Http2ResponseWriter, ValueTask> handler)
    {
        _streamedHandler = handler;
        return RunBufferedAsync(NoBufferedHandler);
    }

    // Never invoked: a streamed connection hands every ready request to a writer before the
    // buffered path would reach this. It exists only because RunBufferedAsync needs a handler.
    private static Http2Response NoBufferedHandler(Http2Request _)
        => throw new InvalidOperationException("A streamed connection dispatches through its writer.");

    /// <summary>
    /// Hand a ready request to the streamed path, if this connection has one. Called from the
    /// dispatch that made it ready, so the writer's first frames ride that same pass.
    /// </summary>
    private bool TryDispatchStreamed(Http2Request request, PendingRequest pending)
    {
        if (_streamedHandler is null)
        {
            return false;
        }

        Http2ResponseWriter writer = RentWriter(request.StreamId);
        _ = ServeStreamedAsync(_streamedHandler, request, writer, pending);
        return true;
    }

    private async Task ServeStreamedAsync(Func<Http2Request, Http2ResponseWriter, ValueTask> handler,
        Http2Request request, Http2ResponseWriter writer, PendingRequest pending)
    {
        try
        {
            await handler(request, writer);
            await writer.CompleteAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ioxide.http2] request handler faulted: {exception.GetBaseException().Message}");
            try
            {
                await writer.CompleteAsync();
            }
            catch
            {
                // The stream is already unusable; nothing more to say to this peer.
            }
        }
        finally
        {
            // The buffered dispatch retires the stream in its own finally; the streamed one skips
            // that path, so it has to do it here. Without this every response leaks its arena and
            // the connection grows without bound - 20 GB over eight seconds of load, measured.
            _creditWaiters.Remove(writer.StreamId);
            _streams.Remove(writer.StreamId);
            pending.Dispose();
            _writerPool.Push(writer);
        }
    }

    private Http2ResponseWriter RentWriter(int streamId)
    {
        if (_writerPool.TryPop(out Http2ResponseWriter? pooled))
        {
            pooled.Reset(streamId);
            return pooled;
        }
        return new Http2ResponseWriter(this, streamId);
    }

    /// <summary>Headers of a streamed response - no END_STREAM, the body follows.</summary>
    internal void SendStreamedHeaders(int streamId, Http2Response response)
        => WriteHeaders(streamId, response, endStream: false);

    /// <summary>
    /// How many body bytes may be sent on this stream right now: the smaller of the connection
    /// window, the stream window and the peer's maximum frame size. Zero means wait.
    /// </summary>
    internal int SendCredit(int streamId)
    {
        if (IsBroken)
        {
            return 0;
        }

        int credit = Math.Min(_peerConnectionWindow, _peerMaxFrameSize);
        if (_streams.TryGetValue(streamId, out PendingRequest? pending))
        {
            credit = Math.Min(credit, pending.SendWindow);
        }
        return Math.Max(credit, 0);
    }

    /// <summary>One DATA frame, already known to fit both windows.</summary>
    internal void SendStreamedData(int streamId, ReadOnlySpan<byte> body, bool endStream)
    {
        Span<byte> header = stackalloc byte[FrameHeader.Size];
        new FrameHeader(body.Length, FrameType.Data,
            endStream ? FrameFlags.EndStream : FrameFlags.None, streamId).Write(header);

        Stage(header);
        if (!body.IsEmpty)
        {
            Stage(body);
        }

        _peerConnectionWindow -= body.Length;
        if (_streams.TryGetValue(streamId, out PendingRequest? pending))
        {
            pending.SendWindow -= body.Length;
        }
    }

    /// <summary>Push whatever is staged out to the transport.</summary>
    internal ValueTask FlushOutboundAsync() => FlushAsync();

    /// <summary>
    /// True while the read loop still owes a flush. A streamed writer checks this to decide whether
    /// to write for itself: inside a pass the answer is no, and skipping that write is what lets
    /// many responses leave on ONE transport write.
    ///
    /// It matters because the cost here is per-write, not per-byte - a streamed response measured
    /// the same whether it carried 2 bytes or 8 KiB, which is the shape of a syscall rather than of
    /// work. Buffered spread one write across every stream in flight; streamed paid one each, and
    /// that alone was the 12.8x.
    /// </summary>
    internal bool PassFlushPending => _passFlushPending;

    private bool _passFlushPending;
    private int _stagedBytes;

    /// <summary>
    /// Write only when nothing else will. A handler that parked - on credit, on a timer, on a
    /// database - resumes after the pass flush has gone by, so its bytes would otherwise sit staged
    /// until the peer happened to send something, which for an endless response is never.
    /// </summary>
    internal ValueTask MaybeFlushAsync()
    {
        // Coalescing has to stay BOUNDED, and not for memory reasons. A producer that loops without
        // awaiting anything of its own - generating as fast as it can - has no yield point except
        // this write, so skipping it unconditionally spins the reactor and the response never moves
        // at all. Past the limit the write happens for real, which both drains the staging and
        // hands the thread back.
        //
        // A PACED producer (an SSE feed waiting on an event) never reaches the limit: it parks, the
        // pass flush carries its chunk out immediately, and it resumes outside the pass where this
        // returns a real flush. So latency stays tight where it matters, and only a bandwidth-bound
        // producer trades a little of it for throughput.
        if (!_passFlushPending || _stagedBytes >= CoalesceLimit)
        {
            return FlushAsync();
        }
        return ValueTask.CompletedTask;
    }

    private const int CoalesceLimit = 16 * 1024;

    /// <summary>Completes when this stream has credit again, or the connection gives up.</summary>
    internal Task WaitForSendCreditAsync(int streamId)
    {
        if (IsBroken)
        {
            return Task.CompletedTask;
        }

        if (!_creditWaiters.TryGetValue(streamId, out List<TaskCompletionSource>? waiters))
        {
            waiters = [];
            _creditWaiters[streamId] = waiters;
        }

        var waiter = new TaskCompletionSource();
        waiters.Add(waiter);
        return waiter.Task;
    }

    /// <summary>
    /// A WINDOW_UPDATE arrived. Stream 0 credits the connection, so it can unblock every parked
    /// writer; anything else unblocks only its own.
    /// </summary>
    private void ReleaseCreditWaiters(int streamId)
    {
        if (_creditWaiters.Count == 0)
        {
            return;
        }

        if (streamId == 0)
        {
            foreach (List<TaskCompletionSource> waiters in _creditWaiters.Values.ToArray())
            {
                Release(waiters);
            }
            _creditWaiters.Clear();
            return;
        }

        if (_creditWaiters.Remove(streamId, out List<TaskCompletionSource>? forStream))
        {
            Release(forStream);
        }

        static void Release(List<TaskCompletionSource> waiters)
        {
            foreach (TaskCompletionSource waiter in waiters)
            {
                waiter.TrySetResult();
            }
        }
    }

    private void ReleaseAllCreditWaiters()
    {
        foreach (List<TaskCompletionSource> waiters in _creditWaiters.Values)
        {
            foreach (TaskCompletionSource waiter in waiters)
            {
                waiter.TrySetResult();
            }
        }
        _creditWaiters.Clear();
    }
}
