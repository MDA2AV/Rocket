namespace ioxide;

/// <summary>TCP-specific ABI: stream socket type, protocol options, and send flags.</summary>
public static unsafe partial class Native {
    public const int SOCK_STREAM = 1;
    public const int IPPROTO_TCP = 6;
    public const int TCP_NODELAY = 1;

    // TODO: Investigate this flag
    // MSG_WAITALL on an OP_SEND makes the kernel retry short sends internally,
    // so a full buffer is acked by a single CQE instead of a userspace
    // resubmit round-trip per partial send.
    public const uint MSG_WAITALL = 0x100;
}
