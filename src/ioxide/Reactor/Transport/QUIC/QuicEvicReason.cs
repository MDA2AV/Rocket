namespace ioxide;

/// <summary>Why the transport dropped a connection it was tracking.</summary>
public enum QuicEvictReason
{
    IdleTimeout,
    ReactorShutdown,
}
