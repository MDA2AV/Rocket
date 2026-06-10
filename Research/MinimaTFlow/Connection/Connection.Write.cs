using System.Buffers;
using System.Runtime.InteropServices;
using MinimaTFlow.Utils;
using static MinimaTFlow.Native;

// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace MinimaTFlow;

/// <summary>
/// Twinflow-style write path: handler thread calls libc <c>send()</c> directly,
/// keeping the io_uring reactor on the recv side only. No flush IVTS, no MPSC
/// hand-off, no send CQE — the response goes straight to the kernel via a
/// single syscall on whichever thread the handler is running on.
/// </summary>
public sealed unsafe partial class Connection : IBufferWriter<byte>
{
    private readonly int _writeSlabSize;
    internal byte* WriteBuffer;
    internal int   WriteTail;

    private readonly UnmanagedMemoryManager _manager;

    // IBufferWriter<byte>
#region IBufferWriter<byte>

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        int remaining = _writeSlabSize - WriteTail;
        if (sizeHint > remaining)
        {
            throw new InvalidOperationException("Buffer too small.");
        }
        return _manager.Memory.Slice(WriteTail, remaining);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (WriteTail + sizeHint > _writeSlabSize)
        {
            throw new InvalidOperationException("Write buffer too small.");
        }
        return new Span<byte>(WriteBuffer + WriteTail, _writeSlabSize - WriteTail);
    }

    public void Advance(int count) => WriteTail += count;

#endregion

    public void Write(ReadOnlySpan<byte> source)
    {
        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
        {
            throw new InvalidOperationException("Write buffer too small.");
        }
        source.CopyTo(new Span<byte>(WriteBuffer + WriteTail, len));
        WriteTail += len;
    }

    /// <summary>
    /// Synchronously send everything we've buffered via libc <c>send()</c>.
    /// Returns a completed ValueTask in the common case; on EAGAIN, spin-yields
    /// the thread until the kernel send buffer drains. No reactor handoff, no
    /// IVTS — the syscall happens on the handler thread.
    ///
    /// Async fallback for EAGAIN is omitted because the class is `unsafe` and
    /// C# disallows `await` in unsafe context. For HTTP/1.1 plaintext on
    /// loopback EAGAIN is essentially never hit; if you serve large bodies,
    /// extract the slow path to a non-unsafe helper.
    /// </summary>
    public ValueTask FlushAsync()
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return default;
        }

        int target = WriteTail;
        if (target == 0)
        {
            return default;
        }

        int off = 0;
        while (off < target)
        {
            int sent = TrySend(WriteBuffer + off, (uint)(target - off), out bool wouldBlock, out bool closed);
            if (closed)
            {
                MarkClosed();
                WriteTail = 0;
                return default;
            }
            if (sent > 0)
            {
                off += sent;
                continue;
            }
            if (wouldBlock)
            {
                if (Volatile.Read(ref _closed) == 1)
                {
                    WriteTail = 0;
                    return default;
                }
                Thread.Yield();
            }
        }

        WriteTail = 0;
        return default;
    }

    private int TrySend(byte* buf, uint len, out bool wouldBlock, out bool closed)
    {
        wouldBlock = false;
        closed = false;
        long n = send(ClientFd, buf, len, MSG_NOSIGNAL);
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
}
