using System.Runtime.InteropServices;
using static ioxide.Native;

namespace ioxide;

// Loop - SharedRing
public sealed unsafe partial class Reactor
{
    private void InitSharedRingBuffer()
    {
        nuint ringBytes = (nuint)_bufferRingEntries * 16;
        _bufRing = (byte*)NativeMemory.AlignedAlloc(ringBytes, 4096);
        NativeMemory.Clear(_bufRing, ringBytes);

        nuint slabBytes = _bufferRingEntries * (nuint)_recvBufferSize;
        _bufSlab = (byte*)NativeMemory.AlignedAlloc(slabBytes, 64);

        _bufRingMask = _bufferRingEntries - 1;

        var reg = new io_uring_buf_reg {
            ring_addr    = (ulong)_bufRing,
            ring_entries = _bufferRingEntries,
            bgid         = BgId,
        };

        int ret = io_uring_register(_ring.Fd, IORING_REGISTER_PBUF_RING, &reg, 1);
        if (ret < 0)
        {
            int err = Marshal.GetLastPInvokeError();

            throw new InvalidOperationException($"register pbuf_ring failed: ret={ret} errno={err}");
        }

        // Slot 0 overlaps the ring's tail field at offset 14; writing only addr/len/bid
        // (offsets 0..13) keeps tail zero until published explicitly.
        for (ushort bid = 0; bid < _bufferRingEntries; bid++) {
            byte* slot = _bufRing + (uint)bid * 16;
            *(ulong*)(slot + 0)  = (ulong)(_bufSlab + bid * (nuint)_recvBufferSize);
            *(uint*)(slot + 8)   = _recvBufferSize;
            *(ushort*)(slot + 12) = bid;
        }
        _bufRingTail = (ushort)_bufferRingEntries;

        PublishBufRingTail();
    }
    
    private void LoopSharedRing()
    {
        while (!_stopRequested)
        {
            DrainReturnQ();
            DrainFlushQ();
            DrainRecycleQ();
            DrainRemoteOps();
            DrainPostQ();

            int rc = _ring.SubmitAndWait(1);
            if (rc < 0 && rc != -EINTR && rc != -EAGAIN && rc != -EBUSY)
            {
                Console.Error.WriteLine($"[r{_id}] io_uring_enter failed: {rc}");
                break;
            }

            uint ready = _ring.CqReady();
            for (uint i = 0; i < ready; i++)
            {
                DispatchSharedRing(in _ring.CqeAt(i));
            }
            _ring.CqAdvance(ready);
        }
    }
    
    private void DispatchSharedRing(in IoUringCqe cqe)
    {
        byte   kind = (byte)(cqe.user_data >> KindShift);
        ushort gen  = (ushort)(cqe.user_data >> GenShift);
        int    fd   = (int)(uint)cqe.user_data;
        bool   more = (cqe.flags & IORING_CQE_F_MORE) != 0;

        switch (kind)
        {
            case KindRecv:
            {
                bool   hasBuf = (cqe.flags & IORING_CQE_F_BUFFER) != 0;
                ushort bid    = hasBuf ? (ushort)(cqe.flags >> IORING_CQE_BUFFER_SHIFT) : (ushort)0;

                Connection? conn = ConnAt(fd, gen);

                if (cqe.res <= 0)
                {
                    // Peer EOF or recv error - reactor owns teardown.
                    if (hasBuf)
                    {
                        ReturnBufferDirect(bid);
                    }
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
                    // Stale CQE from the fd's previous tenant.
                    if (hasBuf)
                    {
                        ReturnBufferDirect(bid);
                    }
                    return;
                }

                byte* ptr = hasBuf ? _bufSlab + (nuint)bid * (nuint)_recvBufferSize : null;
                if (!conn.Complete(cqe.res, bid, hasBuf, ptr))
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
                    SubmitRecvMultishot(fd, gen, BgId);
                }
                return;
            }

            case KindSend:
                OnSendCompletion(fd, gen, cqe.res, cqe.flags);
                return;

            case KindClient:
                OnClientCompletion(fd, cqe.res);   // low 32 bits = op slot
                return;

            case KindAccept:
            {
                if (cqe.res >= 0)
                {
                    int clientFd = cqe.res;
                    SetNoDelay(clientFd);
                    Connection conn = _pool.TryPop(out var pooled)
                        ? pooled.SetFd(clientFd)
                        : new Connection(this, clientFd, _config.WriteSlabSize, _config.RecvQueueEntries, _config.WriteOverflow);
                    Track(clientFd, conn);
                    conn.InitRefs();
                    conn.UseZc = _zeroCopySend;   // config default; kTLS overrides to plain on handshake
                    conn.ListenerPort = PortOf(fd);
                    SubmitRecvMultishot(clientFd, (ushort)conn.Generation, BgId);

                    _ = RunHandlerAsync(conn);
                }
                else
                {
                    Console.Error.WriteLine($"[r{_id}] accept error: {cqe.res}");
                }
                if (!more)
                {
                    SubmitAcceptMultishot(fd);
                }
                return;
            }

            case KindUdpRecv:
                OnUdpRecvCompletion(fd, cqe.res);   // low 32 bits = recv-slot index
                return;

            case KindUdpSend:
                OnUdpSendCompletion(fd, cqe.res);   // low 32 bits = send-slot index
                return;

            case KindWake:
                OnWakeCompletion(more);
                return;

            case KindTimer:
                OnTimerTick();
                return;

            case KindCancel:
                return;
        }
    }
}