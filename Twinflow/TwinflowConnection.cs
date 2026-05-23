// ReSharper disable SuggestVarOrType_SimpleTypes
// ReSharper disable SuggestVarOrType_Elsewhere
// ReSharper disable SuggestVarOrType_BuiltInTypes
namespace Twinflow;

/// <summary>
/// Lean connection: dual BCL Pipes + a libc-send pump, with recv driven by the
/// io_uring reactor. The reactor copies recv bytes into Input; Kestrel reads Input
/// and writes Output; the pump drains Output and sends via a thread-safe libc send().
/// </summary>
internal sealed class TwinflowConnection
{
    public readonly int Fd;
    public readonly long Id;
    private readonly TwinflowReactor _reactor;

    public readonly Pipe Input;
    public readonly Pipe Output;

    private int _refs = 2;     // reactor (recv) side + pump side
    private int _closed;
    private static long s_id;

    public TwinflowConnection(int fd, TwinflowReactor reactor)
    {
        Fd = fd;
        _reactor = reactor;
        Id = Interlocked.Increment(ref s_id);
        var o = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);
        Input = new Pipe(o);
        Output = new Pipe(o);
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    //Reactor thread: copy recv bytes (from the provided buffer) into Input
    public unsafe void OnRecv(byte* ptr, int len)
    {
        Span<byte> dst = Input.Writer.GetSpan(len);
        new ReadOnlySpan<byte>(ptr, len).CopyTo(dst);
        
        Input.Writer.Advance(len);
        _ = Input.Writer.FlushAsync();   // schedules Kestrel's read on the thread pool
    }

    //Output pump (thread pool): drain Output and send via libc send()
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
                        if (closed)
                        {
                            fail = true;
                            break;
                        }

                        if (sent > 0)
                        {
                            off += sent;
                            continue;
                        }

                        if (wouldBlock)
                        {
                            // EAGAIN is ~never hit for small responses. Yield+retry instead of an
                            // io_uring POLLOUT round-trip. Harden here if you serve large bodies.
                            if (IsClosed)
                            {
                                fail = true;
                                break;
                            }

                            await Task.Yield();
                        }
                    }

                    if (fail)
                    {
                        break;
                    }
                }

                reader.AdvanceTo(buf.End);
                if (fail || r.IsCompleted)
                {
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                await reader.CompleteAsync();
            }
            catch
            {
                // Dindu nuffin
            } 
            DecRef();
        }
    }

    private unsafe int TrySend(ReadOnlySpan<byte> data, out bool wouldBlock, out bool closed)
    {
        wouldBlock = false;
        closed = false;
        if (data.IsEmpty)
        {
            return 0;
        }

        long n;
        fixed (byte* p = data)
        {
            n = send(Fd, p, (nuint)data.Length, MSG_NOSIGNAL);
        }
        if (n > 0)
        {
            return (int)n;
        }

        int err = (n == 0) ? EAGAIN : Marshal.GetLastPInvokeError();
        if (err is EAGAIN or EWOULDBLOCK)
        {
            wouldBlock = true; 
            return 0;
        }
        if (err == EINTR)
        {
            return 0;
        }
        
        closed = true;
        
        return 0;
    }

    public void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }
        try
        {
            Input.Writer.Complete();
        }
        catch
        {
            // Dindu nuffin
        }
    }

    public void DecRef()
    {
        if (Interlocked.Decrement(ref _refs) != 0)
        {
            return;
        }
        _reactor.Remove(this);
        
        close(Fd);
    }

    /// <summary>
    /// Kestrel finished/aborted the connection. Complete the pipes (pump exits) and
    /// shutdown() the socket so the reactor's outstanding multishot recv completes and
    /// releases its ref. The fd is closed once both refs drop to 0.
    /// </summary>
    public void OnKestrelClose()
    {
        MarkClosed();
        try
        {
            Output.Writer.Complete();
        }
        catch
        {
            
        }
        shutdown(Fd, SHUT_RDWR);
    }
}
