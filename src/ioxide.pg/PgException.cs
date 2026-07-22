namespace ioxide.pg;

/// <summary>
/// A Postgres failure. Server-reported errors (an ErrorResponse) carry the severity and SQLSTATE;
/// transport failures carry the failed operation and errno. Server errors leave the connection
/// usable (the protocol resynchronizes at ReadyForQuery); transport failures mark it broken and
/// the pool replaces it.
/// </summary>
public sealed class PgException : Exception
{
    /// <summary>Server-reported severity ("ERROR", "FATAL"), or null for client/transport failures.</summary>
    public string? Severity { get; }

    /// <summary>The SQLSTATE error code (e.g. "42P01"), or null for client/transport failures.</summary>
    public string? SqlState { get; }

    public PgException(string message) : base(message)
    {
    }

    public PgException(string severity, string sqlState, string message)
        : base($"{severity} {sqlState}: {message}")
    {
        Severity = severity;
        SqlState = sqlState;
    }

    internal static PgException Transport(string operation, int result)
    {
        return result == 0
            ? new PgException($"{operation}: server closed the connection")
            : new PgException($"{operation} failed: errno {-result}");
    }
}
