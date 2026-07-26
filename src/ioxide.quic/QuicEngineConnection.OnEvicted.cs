using ioxide;

namespace ioxide.quic;

/// <summary>Transport dropped the connection (idle sweep or shutdown).</summary>
public unsafe partial class QuicEngineConnection
{
    public override void OnEvicted(QuicEvictReason reason)
    {
        Destroy();
    }
}
