namespace ioxide.h3;

/// <summary>
/// One HTTP/3 request, fully assembled (headers + body) before the handler runs. The surface is
/// post-QPACK BYTES throughout - the library never decodes text; convert at the edge if you need
/// strings (<c>Encoding.ASCII.GetString(req.Path.Span)</c>), or route by byte compare
/// (<c>req.Path.Span.SequenceEqual("/x"u8)</c>). Every memory slices a per-request buffer and is
/// guaranteed valid only until the handler returns - don't stash one past that (a future
/// zero-copy backing keeps the same contract).
/// </summary>
public sealed class H3Request
{
    public long StreamId { get; internal set; }

    /// <summary>":method" pseudo-header bytes, e.g. GET.</summary>
    public ReadOnlyMemory<byte> Method { get; internal set; }

    /// <summary>":path" pseudo-header bytes, e.g. /plaintext.</summary>
    public ReadOnlyMemory<byte> Path { get; internal set; }

    public ReadOnlyMemory<byte> Scheme { get; internal set; }
    public ReadOnlyMemory<byte> Authority { get; internal set; }

    /// <summary>Non-pseudo headers; names arrive lowercase (h3 wire requirement), values are raw octets.</summary>
    public List<(ReadOnlyMemory<byte> Name, ReadOnlyMemory<byte> Value)> Headers { get; } = [];

    public ReadOnlyMemory<byte> Body { get; internal set; }

    // --- assembly state (reactor thread, inside ih3_read_stream) -------------------------------
    // Header bytes are copied into one growable arena as the callbacks fire (nghttp3 reclaims its
    // buffers when each callback returns). Only (offset, length) ranges are recorded during
    // assembly: growing the arena reallocates the array, which would strand any Memory handed out
    // earlier. Freeze() materializes the public memories once the arena is final, at dispatch.

    internal byte[] Arena = [];
    internal int ArenaUsed;
    internal (int Off, int Len) MethodR = (0, -1), PathR = (0, -1), SchemeR = (0, -1), AuthorityR = (0, -1);
    internal readonly List<(int NOff, int NLen, int VOff, int VLen)> HeaderRanges = [];
    internal MemoryStream? BodyBuffer;
    internal bool Complete;

    internal unsafe (int Off, int Len) Append(byte* p, int len)
    {
        if (Arena.Length - ArenaUsed < len)
        {
            int size = Math.Max(1024, Arena.Length * 2);
            while (size - ArenaUsed < len)
            {
                size *= 2;
            }
            Array.Resize(ref Arena, size);
        }

        new ReadOnlySpan<byte>(p, len).CopyTo(Arena.AsSpan(ArenaUsed));
        (int Off, int Len) range = (ArenaUsed, len);
        ArenaUsed += len;
        return range;
    }

    internal void Freeze()
    {
        Method    = Slice(MethodR);
        Path      = Slice(PathR);
        Scheme    = Slice(SchemeR);
        Authority = Slice(AuthorityR);

        foreach ((int nOff, int nLen, int vOff, int vLen) in HeaderRanges)
        {
            Headers.Add((Arena.AsMemory(nOff, nLen), Arena.AsMemory(vOff, vLen)));
        }
        HeaderRanges.Clear();

        if (BodyBuffer is not null)
        {
            // Wrap the stream's own buffer - no ToArray copy; the array stays alive via the Memory.
            Body = BodyBuffer.GetBuffer().AsMemory(0, (int)BodyBuffer.Length);
            BodyBuffer = null;
        }
    }

    private ReadOnlyMemory<byte> Slice((int Off, int Len) r) => r.Len < 0 ? default : Arena.AsMemory(r.Off, r.Len);
}

/// <summary>
/// One HTTP/3 response: status, headers, and an in-memory body - bytes throughout, mirroring
/// <see cref="H3Request"/>. Header names must be ASCII (they're lowercased as they're packed);
/// values are raw octets. Everything is copied into nghttp3 synchronously at submit, so the
/// memories can be pooled, stackallocated behind, or static.
/// </summary>
public sealed class H3Response
{
    public int Status { get; init; } = 200;
    public List<(ReadOnlyMemory<byte> Name, ReadOnlyMemory<byte> Value)> Headers { get; } = [];
    public ReadOnlyMemory<byte> Body { get; init; }

    private static readonly byte[] ContentTypeName = "content-type"u8.ToArray();
    private static readonly byte[] TextPlainValue  = "text/plain; charset=utf-8"u8.ToArray();

    /// <summary>
    /// Convenience for text bodies: UTF-8 encodes and stamps content-type. The rest of the
    /// surface stays byte-level - build responses from bytes directly for the allocation-lean path.
    /// </summary>
    public static H3Response Text(string body, int status = 200)
    {
        var response = new H3Response
        {
            Status = status,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
        };

        response.Headers.Add((ContentTypeName, TextPlainValue));

        return response;
    }
}
