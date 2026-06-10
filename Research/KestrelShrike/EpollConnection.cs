namespace KestrelShrike;

/// <summary>
/// One TCP connection bridged to Kestrel through two BCL Pipes:
///   - Input:  the reactor drains recv into Input.Writer; Kestrel reads Input.Reader.
///   - Output: Kestrel writes Output.Writer; the per-connection pump reads Output.Reader
///             and sends — DIRECTLY from the thread-pool thread, because an epoll
///             socket's send() is thread-safe. No reactor handoff (unlike io_uring's
///             single-issuer ring). The reactor is only involved on EAGAIN (arm
///             EPOLLOUT, signal the pump when writable).
/// Lifetime: 2-ref count (reactor/recv side + pump side); the fd closes when both end.
/// </summary>
internal sealed class EpollConnection
{
    public readonly int Fd;
    public readonly int Ep;
    private readonly EpollReactor _reactor;

    public readonly Pipe Input;
    public readonly Pipe Output;

    private TaskCompletionSource<bool>? _writable;   // set while the pump waits for EPOLLOUT
    private int _refs = 2;
    private int _closed;

    private const int RecvChunk = 16 * 1024;

    public EpollConnection(int fd, int ep, EpollReactor reactor)
    {
        Fd = fd;
        Ep = ep;
        _reactor = reactor;
        var o = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);
        Input = new Pipe(o);
        Output = new Pipe(o);
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    // ---- recv (reactor thread): drain into Input.Writer. False => peer closed / error. ----
    public unsafe bool OnReadable()
    {
        if (IsClosed) return false;

        bool any = false;
        bool ok = true;
        while (true)
        {
            Span<byte> span = Input.Writer.GetSpan(RecvChunk);
            long n;
            fixed (byte* p = span) n = recv(Fd, p, (ulong)span.Length, 0);

            if (n > 0) { Input.Writer.Advance((int)n); any = true; continue; }
            if (n == 0) { ok = false; break; }                 // peer closed

            int err = Marshal.GetLastPInvokeError();
            if (err is EAGAIN or EWOULDBLOCK) break;            // drained
            if (err == EINTR) continue;
            ok = false; break;                                  // hard error
        }

        if (any) _ = Input.Writer.FlushAsync();
        return ok;
    }

    // ---- output pump (thread pool) ----
    public async Task RunOutputPump()
    {
        PipeReader reader = Output.Reader;
        try
        {
            while (true)
            {
                ReadResult r = await reader.ReadAsync().ConfigureAwait(false);
                if (r.IsCanceled) break;

                ReadOnlySequence<byte> buf = r.Buffer;
                bool fail = false;

                foreach (ReadOnlyMemory<byte> seg in buf)
                {
                    int off = 0;
                    while (off < seg.Length)
                    {
                        int sent = TrySend(seg.Span.Slice(off), out bool wouldBlock, out bool closed);
                        if (closed) { fail = true; break; }
                        if (sent > 0) { off += sent; continue; }
                        if (wouldBlock && !await WaitWritableAsync().ConfigureAwait(false)) { fail = true; break; }
                        // EINTR (sent == 0, not wouldBlock, not closed) just retries
                    }
                    if (fail) break;
                }

                reader.AdvanceTo(buf.End);
                if (fail || r.IsCompleted) break;
            }
        }
        catch { /* connection died mid-send */ }
        finally { try { reader.Complete(); } catch { } DecRef(); }
    }

    private unsafe int TrySend(ReadOnlySpan<byte> data, out bool wouldBlock, out bool closed)
    {
        wouldBlock = false;
        closed = false;
        if (data.IsEmpty) return 0;

        long n;
        fixed (byte* p = data) n = send(Fd, p, data.Length, MSG_NOSIGNAL);
        if (n > 0) return (int)n;

        int err = (n == 0) ? EAGAIN : Marshal.GetLastPInvokeError();
        if (err is EAGAIN or EWOULDBLOCK) { wouldBlock = true; return 0; }
        if (err == EINTR) return 0;
        closed = true;
        return 0;
    }

    // ---- EAGAIN: arm EPOLLOUT and wait for the reactor's writable signal ----
    private Task<bool> WaitWritableAsync()
    {
        if (IsClosed) return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _writable, tcs);
        ArmEpollOut();                              // epoll_ctl is thread-safe
        if (IsClosed) tcs.TrySetResult(false);      // raced with close
        return tcs.Task;
    }

    public void SignalWritable()   // reactor: EPOLLOUT fired
    {
        TaskCompletionSource<bool>? tcs = Interlocked.Exchange(ref _writable, null);
        if (tcs is not null)
        {
            ArmEpollIn();
            tcs.TrySetResult(true);
        }
    }

    public void MarkClosed()   // reactor thread: completes Input.Writer (sole writer)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        try { Input.Writer.Complete(); } catch { }
        Interlocked.Exchange(ref _writable, null)?.TrySetResult(false);   // unblock the pump
    }

    public void DecRef()
    {
        if (Interlocked.Decrement(ref _refs) != 0) return;
        _reactor.Remove(this);
        close(Fd);
    }

    private unsafe void ArmEpollOut()
    {
        byte* ev = stackalloc byte[EvSize];
        WriteEpollEvent(ev, (uint)(EPOLLIN | EPOLLOUT | EPOLLRDHUP | EPOLLERR | EPOLLHUP) | EPOLLET, Fd);
        epoll_ctl(Ep, EPOLL_CTL_MOD, Fd, (IntPtr)ev);
    }

    private unsafe void ArmEpollIn()
    {
        byte* ev = stackalloc byte[EvSize];
        WriteEpollEvent(ev, (uint)(EPOLLIN | EPOLLRDHUP | EPOLLERR | EPOLLHUP) | EPOLLET, Fd);
        epoll_ctl(Ep, EPOLL_CTL_MOD, Fd, (IntPtr)ev);
    }
}
