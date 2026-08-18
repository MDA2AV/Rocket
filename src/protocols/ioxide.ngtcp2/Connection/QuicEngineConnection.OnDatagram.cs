namespace ioxide.ngtcp2;

/// <summary>Ingress: datagrams routed by the transport are fed to ngtcp2 here.</summary>
public unsafe partial class QuicEngineConnection
{
    public override void OnDatagram(ReadOnlySpan<byte> payload, byte tos)
    {
        if (_closed)
        {
            return;
        }
        _inEngineCycle = true;
        try
        {
            OnDatagramCore(payload, tos);
        }
        finally
        {
            EndEngineCycle();
        }
    }

    private void OnDatagramCore(ReadOnlySpan<byte> payload, byte tos)
    {
        // One call = one wire datagram: the transport pre-splits GRO trains before demux.
        int rv;

        // TODO: can we get the byte* without a fixed
        fixed (byte* p = payload)
        {
            // milestone: no migration - the path is fixed at accept, so remote_sa is unused.
            rv = Ngtcp2.iq_conn_read(_conn, null, 0, p, (nuint)payload.Length, tos, NowNs());
        }
        if (rv != 0)
        {
            CloseFromEngine(rv);
            return;
        }
        if (!_handshakeDone && Ngtcp2.iq_conn_is_established(_conn) != 0)
        {
            HandshakeCompletedOnce();
        }

        FlushEgress();
        FireRecv();
        FireHandshakeSignal();
    }
}
