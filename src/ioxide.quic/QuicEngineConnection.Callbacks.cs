using System.Runtime.InteropServices;
using ioxide;

namespace ioxide.quic;

/// <summary>Unmanaged callbacks from the shim (reactor thread, inside iq_conn_read /
/// handle_expiry / iq_accept).</summary>
public unsafe partial class QuicEngineConnection
{
    // --- unmanaged callbacks from the shim (reactor thread) -----------------------------------

    private static QuicEngineConnection From(void* user)
        => (QuicEngineConnection)GCHandle.FromIntPtr((nint)user).Target!;

    [UnmanagedCallersOnly]
    internal static void CbStreamData(void* user, long streamId, byte* data, nuint len, int fin)
        => From(user).OnStreamData(streamId, new ReadOnlySpan<byte>(data, (int)len), fin != 0);

    [UnmanagedCallersOnly]
    internal static void CbStreamClose(void* user, long streamId, ulong appError)
    {
        QuicEngineConnection c = From(user);
        c.PurgeOutStream(streamId);
        c.EnqueueLifecycle(streamId, QuicStreamEvent.Closed, appError);
        c.OnStreamClosed(streamId, appError);
    }

    [UnmanagedCallersOnly]
    internal static void CbAckedStreamData(void* user, long streamId, ulong offset, ulong datalen)
        => From(user).OnAckedStreamData(streamId, offset, datalen);

    [UnmanagedCallersOnly]
    internal static void CbStreamReset(void* user, long streamId, ulong appError)
        => From(user).EnqueueLifecycle(streamId, QuicStreamEvent.Reset, appError);

    [UnmanagedCallersOnly]
    internal static void CbStreamStopSending(void* user, long streamId, ulong appError)
    {
        QuicEngineConnection c = From(user);
        c.MarkOutStreamDead(streamId);
        c.EnqueueLifecycle(streamId, QuicStreamEvent.StopSending, appError);
    }

    [UnmanagedCallersOnly]
    internal static void CbHandshakeCompleted(void* user)
    {
        // Established flips in OnDatagram too; this fires it precisely at the engine's signal.
        QuicEngineConnection c = From(user);
        if (!c._handshakeDone)
        {
            c.HandshakeCompletedOnce();
        }
    }

    [UnmanagedCallersOnly]
    internal static void CbNewCid(void* user, byte* cid, nuint len)
    {
        QuicEngineConnection c = From(user);
        c._reactor.QuicRegisterCid(c, new QuicCid(new ReadOnlySpan<byte>(cid, (int)len)));
    }

    [UnmanagedCallersOnly]
    internal static void CbRetireCid(void* user, byte* cid, nuint len)
    {
        QuicEngineConnection c = From(user);
        c._reactor.QuicUnregisterCid(new QuicCid(new ReadOnlySpan<byte>(cid, (int)len)));
    }
}
