namespace ioxide;

/// <summary>
/// The contract a reactor implements so ring-native clients (Postgres, files, Redis) can run on
/// it. A client hands each submission an fd, a native buffer, and the completion to call back;
/// the reactor builds the SQE and routes the CQE to that completion - inline, on the reactor
/// thread. Routing is per operation, so concurrent ops on one fd never collide.
///
/// Buffers cross as <see cref="nint"/> because pointers can't survive an await. Safe from any
/// thread (off-reactor callers are marshalled). Results: bytes transferred (0 for connect), or a
/// negative errno.
/// </summary>
public interface IRingHost
{
    /// <summary>Connect <paramref name="fd"/> to the native sockaddr at <paramref name="sockaddr"/>.</summary>
    void SubmitConnect(int fd, nint sockaddr, int sockaddrLen, IRingCompletion completion);

    /// <summary>Send <paramref name="length"/> bytes from <paramref name="buffer"/> on <paramref name="fd"/>.</summary>
    void SubmitSend(int fd, nint buffer, int length, IRingCompletion completion);

    /// <summary>Receive up to <paramref name="length"/> bytes into <paramref name="buffer"/> on <paramref name="fd"/>.</summary>
    void SubmitRecv(int fd, nint buffer, int length, IRingCompletion completion);

    /// <summary>Read up to <paramref name="length"/> bytes into <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    void SubmitRead(int fd, nint buffer, int length, long offset, IRingCompletion completion);

    /// <summary>Write <paramref name="length"/> bytes from <paramref name="buffer"/> at <paramref name="offset"/>.</summary>
    void SubmitWrite(int fd, nint buffer, int length, long offset, IRingCompletion completion);
}

/// <summary>The reactor calls this when a submitted operation's CQE arrives.</summary>
public interface IRingCompletion
{
    /// <param name="result">Bytes transferred, or a negative errno.</param>
    void Complete(int result);
}
