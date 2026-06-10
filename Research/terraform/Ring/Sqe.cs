using System.Runtime.CompilerServices;

namespace terraform.Ring;

/// <summary>
/// Static SQE prep helpers. Since GetSqe() does not zero the SQE,
/// each prep clears the full 64 bytes then sets only the fields it needs.
/// The zero cost is amortized across the field writes that follow immediately.
/// </summary>
internal static unsafe class Sqe
{
    /// <summary>
    /// Prepare a multishot accept SQE.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepMultishotAccept(IoUringSqe* sqe, int fd, uint flags)
    {
        Unsafe.InitBlockUnaligned(sqe, 0, (uint)sizeof(IoUringSqe));
        sqe->opcode   = Constants.IORING_OP_ACCEPT;
        sqe->fd       = fd;
        sqe->ioprio   = Constants.IORING_ACCEPT_MULTISHOT;
        sqe->op_flags = flags;
    }

    /// <summary>
    /// Prepare a multishot recv with buffer selection SQE.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepRecvMultishotSelect(IoUringSqe* sqe, int fd, ushort bgid, uint flags)
    {
        Unsafe.InitBlockUnaligned(sqe, 0, (uint)sizeof(IoUringSqe));
        sqe->opcode    = Constants.IORING_OP_RECV;
        sqe->fd        = fd;
        sqe->ioprio    = Constants.IORING_RECV_MULTISHOT;
        sqe->flags     = Constants.IOSQE_BUFFER_SELECT;
        sqe->buf_index = bgid;
        sqe->op_flags  = flags;
    }

    /// <summary>
    /// Prepare a send SQE.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepSend(IoUringSqe* sqe, int fd, byte* buf, uint len, uint flags)
    {
        Unsafe.InitBlockUnaligned(sqe, 0, (uint)sizeof(IoUringSqe));
        sqe->opcode   = Constants.IORING_OP_SEND;
        sqe->fd       = fd;
        sqe->addr     = (ulong)buf;
        sqe->len      = len;
        sqe->op_flags = flags;
    }

    /// <summary>
    /// Prepare an async cancel SQE targeting a specific user_data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepCancel64(IoUringSqe* sqe, ulong userData, uint flags)
    {
        Unsafe.InitBlockUnaligned(sqe, 0, (uint)sizeof(IoUringSqe));
        sqe->opcode   = Constants.IORING_OP_ASYNC_CANCEL;
        sqe->addr     = userData;
        sqe->op_flags = flags;
    }
}
