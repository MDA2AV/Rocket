namespace ioxide;

/// <summary>
/// One completed <see cref="QuicConnection.ReadAsync"/> cycle: drain items up to Tail with
/// <see cref="QuicConnection.TryGetItem"/>. QUIC's mirror of the TCP RecvSnapshot - separate type,
/// the transports share the pattern but no code.
/// </summary>
public readonly struct QuicRecvSnapshot
{
    public readonly long Tail;
    public readonly bool IsClosed;

    public QuicRecvSnapshot(long tail, bool isClosed)
    {
        Tail = tail;
        IsClosed = isClosed;
    }

    public static QuicRecvSnapshot Closed() => new(0, isClosed: true);
}
