using ioxide;

namespace ioxide.ngtcp2;

/// <summary>Transport dropped the connection (idle sweep or shutdown).</summary>
public unsafe partial class QuicEngineConnection
{
    public override void OnEvicted(QuicEvictReason reason)
    {
        Destroy();
    }
}
