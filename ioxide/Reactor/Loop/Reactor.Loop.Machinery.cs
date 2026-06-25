using System.Runtime.CompilerServices;
using static ioxide.Native;

namespace ioxide;

public sealed unsafe partial class Reactor
{
    private void SubmitRecvMultishot(int fd, ushort gen, ushort bgid)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECV;
        sqe->flags     = IOSQE_BUFFER_SELECT;
        sqe->ioprio    = IORING_RECV_MULTISHOT;
        sqe->fd        = fd;
        sqe->buf_index = bgid;
        sqe->user_data = Tag(KindRecv, gen, fd);
    }

    // Dispatch a send to this connection's strategy. A predictable per-connection branch (ZeroCopySend
    // is constant for the run; kTLS pins plain) instead of an indirect call - so SubmitSendImpl stays
    // inlinable on the hot send path.
    private void SubmitSend(Connection conn, int fd, ushort gen, byte* buf, uint len, uint opFlags)
    {
        if (conn.UseZc)
        {
            SubmitSendImpl(this, IORING_OP_SEND_ZC, fd, gen, buf, len, opFlags);
        }
        else
        {
            SubmitSendImpl(this, IORING_OP_SEND, fd, gen, buf, len, opFlags);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SubmitSendImpl(Reactor r, byte opcode, int fd, ushort gen, byte* buf, uint len, uint opFlags)
    {
        IoUringSqe* sqe = r.GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = opcode;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = len;
        sqe->op_flags  = opFlags;   // MSG_WAITALL by default; cleared for kTLS
        sqe->user_data = Tag(KindSend, gen, fd);
    }

    // Vectored send: one SQE gathers every write segment (primary + overflow) from the iovec the
    // connection prepared in BuildIovec. Plain SENDMSG (no zero-copy) for the segmented path.
    private void SubmitSendMsg(Connection conn, int fd, ushort gen)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_SENDMSG;
        sqe->fd        = fd;
        sqe->addr      = (ulong)conn.MsgHdr;
        sqe->len       = 1;
        sqe->op_flags  = conn.SendOpFlags;   // MSG_WAITALL
        sqe->user_data = Tag(KindSend, gen, fd);
    }

    // Cancel by exact user_data so a dead connection's multishot recv can't keep
    // consuming buffers or race the fd's next tenant.
    private void SubmitCancel(ulong targetUserData)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_ASYNC_CANCEL;
        sqe->fd        = -1;
        sqe->addr      = targetUserData;
        sqe->user_data = Tag(KindCancel, 0, 0);
    }
    
}