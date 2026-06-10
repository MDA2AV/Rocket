namespace ioxide;

/// <summary>
/// The contract a thread-per-core io_uring reactor implements so that ring-native clients —
/// a Postgres connection, a file, a Redis socket — can run on it.
///
/// The reactor owns the ring and is its single issuer. A client never touches the ring directly.
/// Instead it hands the reactor a file descriptor, a native buffer address, and a length, and the
/// reactor builds the matching submission. When the completion comes back, the reactor calls the
/// client's <see cref="IRingCompletion.Complete"/> — inline, on the reactor thread, so the
/// awaiting query or read resumes right where it left off.
///
/// Buffers cross this boundary as <see cref="nint"/> (a raw address) rather than a pointer. That
/// matters: an <c>async</c> client method cannot live in an <c>unsafe</c> context, and a pointer
/// cannot survive an <c>await</c> — but an <see cref="nint"/> can.
/// </summary>
public interface IRingHost
{
    /// <summary>
    /// Route every completion for <paramref name="fd"/> to <paramref name="target"/>. A client
    /// calls this once, when it opens its descriptor.
    /// </summary>
    void Bind(int fd, IRingCompletion target);

    // ── Stream I/O: sockets (Postgres, Redis, an HTTP upstream) ──────────────────────────────

    /// <summary>Send <paramref name="length"/> bytes from <paramref name="buffer"/> on <paramref name="fd"/>.</summary>
    void SubmitSend(int fd, nint buffer, int length);

    /// <summary>Receive up to <paramref name="length"/> bytes into <paramref name="buffer"/> on <paramref name="fd"/>.</summary>
    void SubmitRecv(int fd, nint buffer, int length);

    // ── Positional I/O: files. The offset is a byte position within the file ────────────────

    /// <summary>Read up to <paramref name="length"/> bytes into <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    void SubmitRead(int fd, nint buffer, int length, long offset);

    /// <summary>Write <paramref name="length"/> bytes from <paramref name="buffer"/> at <paramref name="offset"/>.</summary>
    void SubmitWrite(int fd, nint buffer, int length, long offset);
}

/// <summary>
/// The other half of the contract. The reactor calls this when a bound descriptor's completion
/// arrives. A client implements it (usually backed by an
/// <see cref="System.Threading.Tasks.Sources.IValueTaskSource{T}"/>) so that the awaiting
/// operation resumes inline.
/// </summary>
public interface IRingCompletion
{
    /// <param name="result">The completion result — bytes transferred, or a negative errno.</param>
    void Complete(int result);
}
