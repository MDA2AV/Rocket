using System.Buffers;
using ioxide.nghttp3;

namespace ioxide.httpclient3;

/// <summary>Method tokens as wire bytes (h3 sends them as the <c>:method</c> pseudo-header).</summary>
public static class Http3Methods
{
    public static readonly byte[] Get    = "GET"u8.ToArray();
    public static readonly byte[] Head   = "HEAD"u8.ToArray();
    public static readonly byte[] Post   = "POST"u8.ToArray();
    public static readonly byte[] Put    = "PUT"u8.ToArray();
    public static readonly byte[] Patch  = "PATCH"u8.ToArray();
    public static readonly byte[] Delete = "DELETE"u8.ToArray();
}

/// <summary>
/// One outgoing HTTP/3 request - bytes throughout, mirroring the server-side surface. The
/// pseudo-headers (<c>:method</c>, <c>:scheme</c>, <c>:authority</c>, <c>:path</c>) are written by
/// the client; add only ordinary fields here, with lowercase names.
/// </summary>
public sealed class Http3ClientRequest
{
    public ReadOnlyMemory<byte> Method { get; init; } = Http3Methods.Get;

    /// <summary>Request target, e.g. <c>"/api/things"u8</c>.</summary>
    public ReadOnlyMemory<byte> Path { get; init; }

    public KeyValueList Headers { get; } = new();

    public ReadOnlyMemory<byte> Body { get; init; }

    public Http3ClientRequest() { }

    public Http3ClientRequest(ReadOnlyMemory<byte> method, ReadOnlyMemory<byte> path)
    {
        Method = method;
        Path = path;
    }

    public Http3ClientRequest(ReadOnlyMemory<byte> method, string path)
        : this(method, System.Text.Encoding.ASCII.GetBytes(path))
    {
    }
}

/// <summary>
/// One HTTP/3 response. Headers and body slice one pooled buffer owned by this object, so the
/// stream is released as soon as the response completes. Dispose returns that buffer; skipping it
/// costs GC pressure, never a stuck stream - but the memories dangle afterwards.
/// </summary>
public sealed class Http3ClientResponse : IDisposable
{
    /// <summary>Status from the <c>:status</c> pseudo-header.</summary>
    public int Status { get; internal set; }

    /// <summary>Response field lines, in wire order (names are lowercase on the h3 wire).</summary>
    public KeyValueList Headers { get; } = new();

    public ReadOnlyMemory<byte> Body { get; internal set; }

    private byte[] _arena = [];
    private int _arenaUsed;
    private readonly List<(int NameOffset, int NameLength, int ValueOffset, int ValueLength)> _ranges = [];
    private (int Offset, int Length) _bodyRange = (0, 0);

    internal (int Offset, int Length) Append(ReadOnlySpan<byte> data)
    {
        if (_arena.Length - _arenaUsed < data.Length)
        {
            int size = Math.Max(2048, _arena.Length * 2);
            while (size - _arenaUsed < data.Length)
            {
                size *= 2;
            }
            byte[] grown = ArrayPool<byte>.Shared.Rent(size);
            _arena.AsSpan(0, _arenaUsed).CopyTo(grown);
            if (_arena.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_arena);
            }
            _arena = grown;
        }

        data.CopyTo(_arena.AsSpan(_arenaUsed));
        (int Offset, int Length) range = (_arenaUsed, data.Length);
        _arenaUsed += data.Length;
        return range;
    }

    internal int ArenaLength => _arenaUsed;

    internal void AddHeaderRange((int Offset, int Length) name, (int Offset, int Length) value)
        => _ranges.Add((name.Offset, name.Length, value.Offset, value.Length));

    internal void SetBodyRange((int Offset, int Length) range) => _bodyRange = range;

    internal void Freeze()
    {
        foreach ((int nameOffset, int nameLength, int valueOffset, int valueLength) in _ranges)
        {
            Headers.Add(_arena.AsMemory(nameOffset, nameLength), _arena.AsMemory(valueOffset, valueLength));
        }
        _ranges.Clear();
        Body = _arena.AsMemory(_bodyRange.Offset, _bodyRange.Length);
    }

    /// <summary>First value for a field name, or false when absent.</summary>
    public bool TryGetHeader(ReadOnlySpan<byte> name, out ReadOnlyMemory<byte> value)
    {
        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in Headers.AsSpan())
        {
            if (field.Key.Span.SequenceEqual(name))
            {
                value = field.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    public void Dispose()
    {
        Headers.Clear();
        Body = default;
        _ranges.Clear();
        _bodyRange = (0, 0);
        _arenaUsed = 0;
        if (_arena.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_arena);
            _arena = [];
        }
    }
}

/// <summary>Any HTTP/3 client failure: connect, handshake, stream, or a protocol error.</summary>
public sealed class Http3ClientException : Exception
{
    public Http3ClientException(string message) : base(message) { }
}
