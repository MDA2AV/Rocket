using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace terraform.Ring;

/// <summary>
/// Buffer ring management. Replaces shim_setup/free/add/advance_buf_ring.
/// </summary>
internal static unsafe class BufRing
{
    /// <summary>
    /// Allocate and register a provided buffer ring with the kernel.
    /// Returns a pointer to the buffer ring header (IoUringBuf array).
    /// </summary>
    public static IoUringBuf* Setup(IoUring ring, uint entries, ushort bgid)
    {
        // Buffer ring must be page-aligned
        nuint size = entries * (nuint)sizeof(IoUringBuf);
        nuint pageSize = 4096;
        void* mem = NativeMemory.AlignedAlloc(size, pageSize);
        NativeMemory.Clear(mem, size);

        IoUringBufReg reg = default;
        reg.ring_addr = (ulong)mem;
        reg.ring_entries = entries;
        reg.bgid = bgid;

        int ret = Syscalls.io_uring_register(ring.Fd, Constants.IORING_REGISTER_PBUF_RING, &reg, 1);
        if (ret < 0)
        {
            NativeMemory.AlignedFree(mem);
            throw new InvalidOperationException($"io_uring_register(REGISTER_PBUF_RING) failed: {ret}");
        }

        return (IoUringBuf*)mem;
    }

    /// <summary>
    /// Unregister and free a provided buffer ring.
    /// </summary>
    public static void Free(IoUring ring, IoUringBuf* br, uint entries, ushort bgid)
    {
        if (br == null) return;

        IoUringBufReg reg = default;
        reg.ring_entries = entries;
        reg.bgid = bgid;

        Syscalls.io_uring_register(ring.Fd, Constants.IORING_UNREGISTER_PBUF_RING, &reg, 1);
        NativeMemory.AlignedFree(br);
    }

    /// <summary>
    /// Add a buffer entry to the ring at the given index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(IoUringBuf* br, byte* addr, uint len, ushort bid, ushort mask, uint index)
    {
        IoUringBuf* buf = &br[index & mask];
        buf->addr = (ulong)addr;
        buf->len = len;
        buf->bid = bid;
    }

    /// <summary>
    /// Advance the buffer ring tail by count entries, making them visible to the kernel.
    /// The tail is stored as a u16 at byte offset 14 within the first IoUringBuf entry (resv field).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Advance(IoUringBuf* br, uint count)
    {
        // The kernel reads the tail from the 'resv' field (u16) of the first buffer ring entry.
        // This is at offset: &br[0].resv = (byte*)br + offsetof(IoUringBuf, resv)
        // IoUringBuf layout: addr(8) + len(4) + bid(2) + resv(2) = offset 14
        ushort* tail = &br->resv;
        ushort currentTail = *tail;
        Volatile.Write(ref *tail, (ushort)(currentTail + count));
    }
}
