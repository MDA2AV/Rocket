using System.Buffers;
using System.IO.Pipelines;
using ioxide;
using ioxide.utils;

namespace ioxide.Kestrel;

/// <summary>
/// A Kestrel transport duplex over an ioxide <see cref="Connection"/>: two BCL <see cref="Pipe"/>s whose
/// reader schedulers route to the reactor thread (via <see cref="ReactorPipeScheduler"/>), plus a
/// recv→inbound pump and an outbound→send pump that run on the reactor. This pins Kestrel's whole request
/// loop to the reactor thread. One copy each way (recv bytes into the inbound pipe; response bytes into
/// the connection slab).
/// </summary>
internal sealed class HopDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly Connection _conn;
    private readonly Reactor _reactor;
    private readonly Pipe _inbound;    // recv pump writes; Kestrel reads (Transport.Input)
    private readonly Pipe _outbound;   // Kestrel writes (Transport.Output); send pump reads

    private Task _recvPump = Task.CompletedTask;
    private Task _sendPump = Task.CompletedTask;
    private int _started;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "shutdown")]
    private static extern int Shutdown(int sockfd, int how);
    private const int ShutWr = 1;   // SHUT_WR

    public PipeReader Input => _inbound.Reader;
    public PipeWriter Output => _outbound.Writer;

    public HopDuplexPipe(Connection conn, Reactor reactor)
    {
        _conn = conn;
        _reactor = reactor;
        var scheduler = new ReactorPipeScheduler(reactor);

        // Reader schedulers = the reactor: Kestrel's HTTP parse (inbound reader) and the send pump
        // (outbound reader) both run on the reactor thread.
        _inbound = new Pipe(new PipeOptions(
            readerScheduler: scheduler,
            writerScheduler: scheduler,
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 512 * 1024,
            useSynchronizationContext: false));

        _outbound = new Pipe(new PipeOptions(
            readerScheduler: scheduler,
            writerScheduler: PipeScheduler.ThreadPool,
            pauseWriterThreshold: 64 * 1024,
            resumeWriterThreshold: 32 * 1024,
            useSynchronizationContext: false));
    }

    /// <summary>Launches the recv and send pumps. Must be called on the reactor thread.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        _recvPump = RecvPumpAsync();
        _sendPump = SendPumpAsync();
    }

    // Reactor → inbound pipe. Copies each recv slice into the pipe and flushes; Kestrel reads it.
    private async Task RecvPumpAsync()
    {
        PipeWriter writer = _inbound.Writer;
        try
        {
            while (true)
            {
                RecvSnapshot snap = await _conn.ReadAsync();

                while (_conn.TryGetItem(snap, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer && item.Len > 0)
                    {
                        CopySlice(in item, writer);
                    }
                    _conn.ReturnBuffer(in item);
                }
                _conn.ResetRead();

                if (snap.IsClosed)
                {
                    break;
                }

                FlushResult fr = await writer.FlushAsync();
                if (fr.IsCompleted || fr.IsCanceled)
                {
                    break;
                }
            }
        }
        catch { /* swallow client/protocol faults; teardown in finally */ }
        finally { await writer.CompleteAsync(); }
    }

    private static unsafe void CopySlice(in SpscRecvRing.Item item, PipeWriter writer)
    {
        Span<byte> dst = writer.GetSpan(item.Len);
        new ReadOnlySpan<byte>(item.Ptr, item.Len).CopyTo(dst);
        writer.Advance(item.Len);
    }

    // Outbound pipe → connection send. Drains Kestrel's response into the slab and submits one SEND.
    private async Task SendPumpAsync()
    {
        PipeReader reader = _outbound.Reader;
        try
        {
            while (true)
            {
                ReadResult rr = await reader.ReadAsync();
                ReadOnlySequence<byte> buffer = rr.Buffer;

                if (!buffer.IsEmpty)
                {
                    foreach (ReadOnlyMemory<byte> segment in buffer)
                    {
                        Span<byte> dst = _conn.GetSpan(segment.Length);
                        segment.Span.CopyTo(dst);
                        _conn.Advance(segment.Length);
                    }
                    await _conn.FlushAsync();
                }

                reader.AdvanceTo(buffer.End);

                if (rr.IsCompleted || rr.IsCanceled)
                {
                    break;
                }
            }
        }
        catch { /* swallow; teardown in finally */ }
        finally { await reader.CompleteAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Quiesce the send side BEFORE closing. Wake the send pump if it's parked on the output reader and
        // await it, so any in-flight SEND completes normally and the full response is flushed. Draining here
        // — rather than concurrently with MarkClosed — is what makes the response reliably reach the client
        // and avoids completing a live flush twice (MarkClosed racing the SEND CQE's CompleteFlush).
        _outbound.Reader.CancelPendingRead();
        try { await _sendPump.ConfigureAwait(false); } catch { }

        // Half-close the write side so EOF-delimited clients (Connection: close / upgrade) see the end of
        // the response — ioxide's refcounted teardown does not FIN a server-initiated close on its own.
        Shutdown(_conn.ClientFd, ShutWr);

        // Now wake and unwind the recv side. MarkClosed wakes a recv parked in conn.ReadAsync — schedule it
        // on the reactor so the recv continuation (reactor-owned state) runs there, not the dispose thread.
        _reactor.ScheduleOnReactor(static c => ((Connection)c!).MarkClosed(), _conn);
        _inbound.Writer.CancelPendingFlush();
        try { await _recvPump.ConfigureAwait(false); } catch { }
    }
}
