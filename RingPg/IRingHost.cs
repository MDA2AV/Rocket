namespace RingPg;

/// <summary>
/// The contract a thread-per-core io_uring reactor implements so ring-native clients (Postgres,
/// Redis, …) can run on it: submit SEND/RECV on a foreign fd and have the CQE routed back to the
/// client's completion — all on the reactor thread, no .NET socket engine, no thread pool.
///
/// The reactor owns the ring (single issuer); the client never touches it. The client hands the
/// reactor a native buffer address (<see cref="nint"/>) and a length; the reactor builds the SQE,
/// tags it so the matching CQE finds its way back, and calls <see cref="IRingCompletion.Complete"/>.
/// </summary>
public interface IRingHost
{
    /// Route CQEs for <paramref name="fd"/> to <paramref name="target"/> (call once per fd).
    void Bind(int fd, IRingCompletion target);

    /// Submit an IORING_OP_SEND of <paramref name="len"/> bytes from <paramref name="buf"/> on <paramref name="fd"/>.
    void SubmitSend(int fd, nint buf, int len);

    /// Submit an IORING_OP_RECV of up to <paramref name="len"/> bytes into <paramref name="buf"/> on <paramref name="fd"/>.
    void SubmitRecv(int fd, nint buf, int len);
}

/// <summary>Completion sink the reactor calls (inline, on the reactor thread) when a bound fd's CQE lands.</summary>
public interface IRingCompletion
{
    void Complete(int result);
}
