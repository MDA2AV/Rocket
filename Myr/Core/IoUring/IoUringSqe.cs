namespace Myr.Core.IoUring;

/// <summary>
/// Submission Queue Entry (64 bytes). Explicit layout for union fields.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct IoUringSqe
{
    [FieldOffset(0)]  public byte   opcode;
    [FieldOffset(1)]  public byte   flags;
    [FieldOffset(2)]  public ushort ioprio;
    [FieldOffset(4)]  public int    fd;
    [FieldOffset(8)]  public ulong  off;            // off / addr2
    [FieldOffset(16)] public ulong  addr;           // addr / splice_off_in
    [FieldOffset(24)] public uint   len;
    [FieldOffset(28)] public uint   op_flags;       // rw_flags / send_flags / accept_flags etc.
    [FieldOffset(32)] public ulong  user_data;
    [FieldOffset(40)] public ushort buf_index;      // buf_index / buf_group
    [FieldOffset(42)] public ushort personality;
    [FieldOffset(44)] public int    splice_fd_in;   // file_index
    [FieldOffset(48)] public ulong  addr3;
    [FieldOffset(56)] public ulong  __pad2;
}

internal static unsafe class IoUringSqeFunctionality
{
    /// <summary>
    /// Prepare a multishot accept SQE.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepMultishotAccept(IoUringSqe* sqe, int fd, uint flags)
    {
        Unsafe.InitBlockUnaligned(sqe, 0, (uint)sizeof(IoUringSqe));
        sqe->opcode   = IORING_OP_ACCEPT;
        sqe->fd       = fd;
        sqe->ioprio   = IORING_ACCEPT_MULTISHOT;
        sqe->op_flags = flags;
    }

    /// <summary>
    /// Prepare a multishot recv with buffer selection SQE.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepRecvMultishotSelect(IoUringSqe* sqe, int fd, ushort bgid, uint flags)
    {
        Unsafe.InitBlockUnaligned(sqe, 0, (uint)sizeof(IoUringSqe));
        sqe->opcode    = IORING_OP_RECV;
        sqe->fd        = fd;
        sqe->ioprio    = IORING_RECV_MULTISHOT;
        sqe->flags     = IOSQE_BUFFER_SELECT;
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
        sqe->opcode   = IORING_OP_SEND;
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
        sqe->opcode   = IORING_OP_ASYNC_CANCEL;
        sqe->addr     = userData;
        sqe->op_flags = flags;
    }
}