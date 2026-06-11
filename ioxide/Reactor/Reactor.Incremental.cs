using System.Runtime.InteropServices;
using ioxide.utils;
using static ioxide.Native;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace ioxide;

/// <summary>
/// Incremental-buffer (IOU_PBUF_RING_INC) path: each connection gets its own buffer ring, and one
/// buffer accumulates its byte stream across many recvs. A buffer recycles only when the kernel is
/// done appending AND the handler returned every slice. Selected by ServerConfig.Incremental.
/// </summary>
public sealed unsafe partial class Reactor
{
    private Stack<ushort>?  _freeGids;
    private Mpsc<ulong>? _returnQInc;

    private void InitIncremental()
    {
        // GID 1 reserved; per-conn GIDs 2..MaxConnections+1.
        _freeGids = new Stack<ushort>(MaxConnections);
        for (int g = MaxConnections + 1; g >= 2; g--)
            _freeGids.Push((ushort)g);

        _returnQInc = new Mpsc<ulong>(1 << 16);
    }

    private ushort AllocGid() => _freeGids!.Pop();
    private void   FreeGid(ushort gid) => _freeGids!.Push(gid);

    private void SetupConnectionBufRing(Connection conn)
    {
        ushort gid = AllocGid();
        int entries = ConnBufRingEntries;

        // Allocations are reused across pool lives; only the kernel registration is per-life.
        if (conn.BufRing == null)
            conn.BufRing = (byte*)NativeMemory.AlignedAlloc((nuint)entries * 16, 4096);
        NativeMemory.Clear(conn.BufRing, (nuint)entries * 16);

        if (conn.BufSlab == null)
            conn.BufSlab = (byte*)NativeMemory.AlignedAlloc((nuint)entries * (nuint)IncRecvBufferSize, 64);

        conn.CumOffset  ??= new int[entries];
        conn.RefCount   ??= new int[entries];
        conn.KernelDone ??= new bool[entries];
        Array.Clear(conn.CumOffset, 0, entries);
        Array.Clear(conn.RefCount, 0, entries);
        Array.Clear(conn.KernelDone, 0, entries);

        var reg = new io_uring_buf_reg
        {
            ring_addr    = (ulong)conn.BufRing,
            ring_entries = (uint)entries,
            bgid         = gid,
            flags        = IOU_PBUF_RING_INC,
        };
        int ret = io_uring_register(Ring.Fd, IORING_REGISTER_PBUF_RING, &reg, 1);
        if (ret < 0)
            throw new InvalidOperationException($"register pbuf_ring (inc) failed: ret={ret} gid={gid}");

        conn.Bgid            = gid;
        conn.BufRingEntries  = entries;
        conn.BufRingMask     = (uint)(entries - 1);
        conn.IncrementalMode = true;

        for (ushort bid = 0; bid < entries; bid++)
        {
            byte* slot = conn.BufRing + (uint)bid * 16;
            *(ulong*)(slot + 0)   = (ulong)(conn.BufSlab + bid * (nuint)IncRecvBufferSize);
            *(uint*)(slot + 8)    = IncRecvBufferSize;
            *(ushort*)(slot + 12) = bid;
        }
        Volatile.Write(ref *(ushort*)(conn.BufRing + 14), (ushort)entries);
    }

    private void TeardownConnectionBufRing(Connection conn)
    {
        if (conn.IncrementalMode)
        {
            // Recycle() cancelled the multishot recv first, so the kernel won't select
            // from this group again before unregister.
            var reg = new io_uring_buf_reg { bgid = conn.Bgid };
            io_uring_register(Ring.Fd, IORING_UNREGISTER_PBUF_RING, &reg, 1);
            FreeGid(conn.Bgid);
        }
        // Allocations stay for pool reuse.
    }

    // Re-add a fully-consumed buffer to its connection's ring. Reactor-thread-only.
    private void ReturnConnectionBuffer(Connection conn, ushort bid)
    {
        conn.CumOffset![bid]  = 0;
        conn.RefCount![bid]   = 0;
        conn.KernelDone![bid] = false;

        ushort tail = Volatile.Read(ref *(ushort*)(conn.BufRing + 14));
        byte* slot  = conn.BufRing + (tail & conn.BufRingMask) * 16;
        *(ulong*)(slot + 0)   = (ulong)(conn.BufSlab + bid * (nuint)IncRecvBufferSize);
        *(uint*)(slot + 8)    = IncRecvBufferSize;
        *(ushort*)(slot + 12) = bid;
        Volatile.Write(ref *(ushort*)(conn.BufRing + 14), (ushort)(tail + 1));
    }

    // Return queue payload: fd in the high 32 bits, gen in the next 16, bid in the low 16.
    private static ulong PackReturn(int fd, ushort gen, ushort bid)
        => ((ulong)(uint)fd << 32) | ((ulong)gen << 16) | bid;

    private static void UnpackReturn(ulong packed, out int fd, out ushort gen, out ushort bid)
    {
        fd  = (int)(packed >> 32);
        gen = (ushort)((packed >> 16) & 0xFFFF);
        bid = (ushort)(packed & 0xFFFF);
    }

    // Safe from any thread.
    public void EnqueueReturnQIncremental(int fd, ushort gen, ushort bid)
    {
        if (Environment.CurrentManagedThreadId == _reactorThreadId)
        {
            ApplyReturnIncremental(fd, gen, bid);
            return;
        }
        ulong packed = PackReturn(fd, gen, bid);
        SpinWait sw = default;
        while (!_returnQInc!.TryEnqueue(packed))
            sw.SpinOnce();
        WakeFdWrite();
    }

    private void DrainReturnQIncremental()
    {
        while (_returnQInc!.TryDequeue(out ulong packed))
        {
            UnpackReturn(packed, out int fd, out ushort gen, out ushort bid);
            ApplyReturnIncremental(fd, gen, bid);
        }
    }

    private void ApplyReturnIncremental(int fd, ushort gen, ushort bid)
    {
        // The gen check also rejects stale returns from a reused fd's previous life.
        Connection? conn = ConnAt(fd, gen);
        if (conn == null || !conn.IncrementalMode)
        {
            return;
        }

        conn.RefCount![bid]--;
        if (conn.RefCount[bid] <= 0 && conn.KernelDone![bid])
        {
            ReturnConnectionBuffer(conn, bid);
        }
    }

    private void LoopIncremental()
    {
        while (true)
        {
            DrainReturnQIncremental();
            DrainFlushQ();
            DrainRecycleQ();
            DrainRemoteOps();

            int rc = Ring.SubmitAndWait(1);
            if (rc < 0 && rc != -EINTR && rc != -EAGAIN && rc != -EBUSY)
            {
                Console.Error.WriteLine($"[r{Id}] io_uring_enter failed: {rc}");

                break;
            }

            uint ready = Ring.CqReady();
            for (uint i = 0; i < ready; i++)
            {
                DispatchIncremental(in Ring.CqeAt(i));
            }
            Ring.CqAdvance(ready);
        }
    }

    private void DispatchIncremental(in IoUringCqe cqe)
    {
        byte   kind = (byte)(cqe.user_data >> KindShift);
        ushort gen  = (ushort)(cqe.user_data >> GenShift);
        int    fd   = (int)(uint)cqe.user_data;
        bool   more = (cqe.flags & IORING_CQE_F_MORE) != 0;

        switch (kind)
        {
            case KindRecv:
            {
                bool   hasBuf  = (cqe.flags & IORING_CQE_F_BUFFER)   != 0;
                bool   bufMore = (cqe.flags & IORING_CQE_F_BUF_MORE) != 0;
                ushort bid     = hasBuf ? (ushort)(cqe.flags >> IORING_CQE_BUFFER_SHIFT) : (ushort)0;

                Connection? conn = ConnAt(fd, gen);

                if (cqe.res <= 0)
                {
                    // Peer EOF / recv error - the per-conn ring is freed in Recycle.
                    if (conn != null)
                    {
                        _connections[fd] = null;
                        conn.MarkClosed();
                        conn.DecRef();
                    }
                    return;
                }

                if (conn == null)
                {
                    return;   // stale CQE; its ring is already gone
                }

                // Data lands at the buffer's running offset; the kernel keeps appending
                // to this bid until the buffer is full (F_BUF_MORE clear).
                byte* ptr = conn.BufSlab + (nuint)bid * (nuint)IncRecvBufferSize + (nuint)conn.CumOffset![bid];
                conn.CumOffset[bid] += cqe.res;
                conn.RefCount![bid]++;
                if (!bufMore || !more)
                {
                    conn.KernelDone![bid] = true;
                }

                if (!conn.Complete(cqe.res, bid, hasBuffer: true, ptr))
                {
                    // Recv queue overflow - tear down rather than zombify.
                    _connections[fd] = null;
                    SubmitCancel(Tag(KindRecv, gen, fd));
                    conn.MarkClosed();
                    conn.DecRef();
                    return;
                }

                if (!more)
                {
                    SubmitRecvMultishot(fd, gen, conn.Bgid);
                }
                return;
            }

            case KindSend:
                OnSendCompletion(fd, gen, cqe.res);
                return;

            case KindClient:
                CompleteClient(fd, cqe.res);
                return;

            case KindAccept:
            {
                if (cqe.res >= 0)
                {
                    int clientFd = cqe.res;
                    SetNoDelay(clientFd);
                    Connection conn = _pool.TryPop(out var pooled)
                        ? pooled.SetFd(clientFd)
                        : new Connection(this, clientFd, _config.WriteSlabSize, _config.RecvQueueEntries);
                    Track(clientFd, conn);
                    conn.InitRefs();
                    conn.ListenerPort = PortOf(fd);
                    SetupConnectionBufRing(conn);
                    SubmitRecvMultishot(clientFd, (ushort)conn.Generation, conn.Bgid);

                    _ = Handle(this, conn);
                }
                else
                {
                    Console.Error.WriteLine($"[r{Id}] accept error: {cqe.res}");
                }
                if (!more)
                {
                    SubmitAcceptMultishot(fd);
                }
                return;
            }

            case KindWake:
                OnWakeCompletion(more);
                return;

            case KindCancel:
                return;
        }
    }
}
