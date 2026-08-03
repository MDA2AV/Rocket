namespace ioxide.ngtcp2;

/// <summary>App-initiated close: the mirror of CloseFromEngine, but the error is ours - a
/// CONNECTION_CLOSE with the given application error code goes out as the connection's last
/// datagram, then the same teardown as every other death.</summary>
public unsafe partial class QuicEngineConnection
{
    public override void Close(ulong applicationErrorCode)
    {
        if (_closed)
        {
            return;
        }

        fixed (byte* dest = _sendBuf)
        {
            nint written = Ngtcp2.iq_conn_close(_conn, applicationErrorCode,
                dest, (nuint)_sendBuf.Length, NowNs());
            if ((int)written > 0)
            {
                Send(_sendBuf.AsSpan(0, (int)written));   // last words: direct, unbatched
            }
        }

        // Close the send gate before the transport wakes the handler (QuicRemoveConnection ->
        // MarkClosed resumes it inline): a farewell SendStream must not pump a dead conn.
        _closed = true;
        _reactor.QuicRemoveConnection(this);
        Destroy();
    }
}
