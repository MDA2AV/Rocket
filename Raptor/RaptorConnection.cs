using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace Raptor;

/// <summary>
/// One TCP connection. Two real BCL Pipes carry bytes — no hand-rolled IVTS:
///   - Input:  reactor copies recv bytes into Input.Writer; Kestrel reads Input.Reader.
///   - Output: Kestrel writes Output.Writer; the per-connection output pump reads
///             Output.Reader and submits the send itself (off-reactor).
/// Send completion is a plain TaskCompletionSource the reactor completes.
/// Lifetime is a 2-ref count (recv side + pump side); the fd/native buffer are
/// released when both are done.
/// </summary>
internal sealed class RaptorConnection
{
    public int Fd { get; }
    public long Id { get; }
    public RaptorReactor Reactor { get; }

    public readonly Pipe Input;
    public readonly Pipe Output;

    internal nint RecvBuf;
    internal readonly int RecvBufSize;

    // Single in-flight send per connection (the pump awaits each before the next).
    private TaskCompletionSource<int>? _sendTcs;
    private MemoryHandle _sendPin;

    private int _refs = 2;       // recv side + pump side
    private int _recvClosed;

    public RaptorConnection(RaptorReactor reactor, int fd, long id, int recvBufSize)
    {
        Reactor = reactor;
        Fd = fd;
        Id = id;
        RecvBufSize = recvBufSize;
        unsafe { RecvBuf = (nint)NativeMemory.AlignedAlloc((nuint)recvBufSize, 64); }

        // No backpressure: the reactor's FlushAsync completes synchronously so it
        // never blocks on the input side.
        var opts = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);
        Input  = new Pipe(opts);
        Output = new Pipe(opts);
    }

    // ---- recv (reactor thread) ----
    internal void OnRecv(int len)
    {
        Span<byte> dst = Input.Writer.GetSpan(len);
        unsafe { new ReadOnlySpan<byte>((byte*)RecvBuf, len).CopyTo(dst); }
        Input.Writer.Advance(len);
        _ = Input.Writer.FlushAsync();   // schedules Kestrel's read on the thread pool
    }

    internal void OnRecvClosed()
    {
        if (Interlocked.Exchange(ref _recvClosed, 1) == 1) return;
        try { Input.Writer.Complete(); } catch { /* already done */ }
        DecRef();
    }

    // ---- send (pump sets pending under the SQ lock; reactor completes) ----
    internal void SetPendingSend(TaskCompletionSource<int> tcs, MemoryHandle pin)
    {
        _sendTcs = tcs;
        _sendPin = pin;
    }

    internal void CompleteSend(int res)
    {
        MemoryHandle pin = _sendPin;
        TaskCompletionSource<int>? tcs = _sendTcs;
        _sendTcs = null;
        _sendPin = default;
        pin.Dispose();                 // unpin the sent segment
        tcs?.TrySetResult(res);        // resumes the pump on the thread pool
    }

    // ---- output pump (thread pool): the connection's HTTP-side thread submits
    //      its own sends, which is the whole point of Raptor. ----
    internal async Task RunOutputPumpAsync()
    {
        PipeReader reader = Output.Reader;
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync().ConfigureAwait(false);
                if (result.IsCanceled) break;

                ReadOnlySequence<byte> buffer = result.Buffer;
                bool failed = false;

                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    ReadOnlyMemory<byte> mem = segment;
                    while (!mem.IsEmpty)
                    {
                        int sent = await Reactor.SendAsync(this, mem).ConfigureAwait(false);
                        if (sent <= 0) { failed = true; break; }
                        mem = mem.Slice(sent);
                    }
                    if (failed) break;
                }

                reader.AdvanceTo(buffer.End);
                if (failed || result.IsCompleted) break;
            }
        }
        catch { /* connection died mid-send */ }
        finally
        {
            try { reader.Complete(); } catch { }
            DecRef();
        }
    }

    // ---- teardown: close fd + free native only when recv AND pump are done ----
    internal void DecRef()
    {
        if (Interlocked.Decrement(ref _refs) != 0) return;
        Reactor.Remove(this);
        Native.close(Fd);
        if (RecvBuf != 0)
        {
            unsafe { NativeMemory.AlignedFree((void*)RecvBuf); }
            RecvBuf = 0;
        }
    }
}
