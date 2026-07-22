namespace ioxide.redis;

/// <summary>A Redis error reply (-ERR ...) or a transport failure.</summary>
public sealed class RedisException : Exception
{
    public RedisException(string message) : base(message) { }

    internal static RedisException Transport(string what, int errno) =>
        new($"redis {what} failed: errno {-errno}");
}
