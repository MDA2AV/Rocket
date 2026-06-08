using System.Runtime.InteropServices;

namespace Rhythm;

/// <summary>
/// Per-connection state. Owned entirely by one reactor thread — there is no
/// cross-thread access, so no synchronization. Buffers are native (off-GC-heap)
/// and reused via the reactor's pool.
///
/// Lifecycle is a strict recv↔send alternation:
///   recv → parse Recv in place → serialize into Write → send → recv …
/// so at most one of {recv, send} is ever in flight for a connection.
/// </summary>
internal sealed unsafe class Connection
{
    public int Fd;

    /// Inbound buffer; RecvLen bytes are currently buffered (carry across recvs).
    public byte* Recv;
    public int RecvLen;

    /// Outbound buffer; WriteLen bytes pending, WriteSent already acked.
    public byte* Write;
    public int WriteLen;
    public int WriteSent;

    /// Close the connection once the current send drains (Connection: close).
    public bool CloseAfter;

    public Connection()
    {
        Recv = (byte*)NativeMemory.Alloc(Cfg.RecvBuf);
        Write = (byte*)NativeMemory.Alloc(Cfg.WriteBuf);
    }

    public void Reset(int fd)
    {
        Fd = fd;
        RecvLen = 0;
        WriteLen = 0;
        WriteSent = 0;
        CloseAfter = false;
    }

    public void FreeNative()
    {
        NativeMemory.Free(Recv);
        NativeMemory.Free(Write);
    }
}
