namespace ioxide;

/// <summary>
/// The connection's file-read facet: read a file descriptor straight into the write slab, so a static
/// file needs no intermediate buffer and no copy - the bytes land exactly where the next send picks
/// them up. IORING_OP_READ on this connection's reactor, resumed inline. One read in flight per
/// connection at a time (reuses a single <see cref="RingOpSource"/>, like the flush facet).
/// </summary>
public sealed unsafe partial class TcpConnection
{
    private readonly RingOpSource _fileRead = new();

    /// <summary>
    /// Read up to <paramref name="len"/> bytes of <paramref name="fileFd"/> from
    /// <paramref name="fileOffset"/> straight into the write slab at the current tail, via
    /// IORING_OP_READ on this connection's reactor. Returns the bytes read (0 at end of file, a
    /// negative errno on error). Grows the slab if it can't hold <paramref name="len"/>.
    ///
    /// The bytes are placed but NOT yet part of the pending write - call <see cref="AdvanceWrite"/>
    /// with the result to commit them before <c>FlushAsync</c>. Framing a header first (with a known
    /// Content-Length) then reading the body after it sends header + body in one flush.
    ///
    /// <b>Cleartext only.</b> The bytes go on the wire as read, so this is for a plain connection. A
    /// TLS connection must read into a buffer and encrypt (<c>TlsSession.Write</c>) - the wire carries
    /// ciphertext, not the file's plaintext.
    /// </summary>
    public ValueTask<int> ReadFileAsync(int fileFd, int len, long fileOffset)
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return new ValueTask<int>(0);
        }

        if (WriteTail + len > _writeSlabSize)
        {
            GrowWriteSlab(WriteTail + len);   // reallocs WriteBuffer; recompute the destination after
        }

        ValueTask<int> pending = _fileRead.Prepare();
        _reactor.SubmitRead(fileFd, (nint)(WriteBuffer + WriteTail), len, fileOffset, _fileRead);
        return pending;
    }

    /// <summary>
    /// Commit <paramref name="n"/> bytes just read by <see cref="ReadFileAsync"/> into the write slab,
    /// so the next <c>FlushAsync</c> sends them. Pass the read's return value; a non-positive result
    /// (EOF or error) advances nothing.
    /// </summary>
    public void AdvanceWrite(int n)
    {
        if (n > 0)
        {
            WriteTail += n;
        }
    }
}
