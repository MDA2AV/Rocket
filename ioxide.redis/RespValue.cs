using System.Buffers.Text;
using System.Text;

namespace ioxide.redis;

public enum RespKind { Null, SimpleString, Error, Integer, BulkString, Array }

/// <summary>
/// A parsed RESP2 reply. Bulk/simple strings and arrays are materialized (they outlive the receive
/// buffer); accessors convert on demand. <see cref="IsNull"/> covers both null bulk strings
/// (<c>$-1</c>) and null arrays (<c>*-1</c>).
/// </summary>
public sealed class RespValue
{
    public static readonly RespValue Null = new(RespKind.Null, 0, null, null);

    public RespKind Kind { get; }
    public long Integer { get; }
    private readonly byte[]? _bytes;
    private readonly RespValue[]? _items;

    private RespValue(RespKind kind, long integer, byte[]? bytes, RespValue[]? items)
    {
        Kind = kind;
        Integer = integer;
        _bytes = bytes;
        _items = items;
    }

    internal static RespValue Simple(byte[] b) => new(RespKind.SimpleString, 0, b, null);
    internal static RespValue Err(byte[] b) => new(RespKind.Error, 0, b, null);
    internal static RespValue Int(long n) => new(RespKind.Integer, n, null, null);
    internal static RespValue Bulk(byte[] b) => new(RespKind.BulkString, 0, b, null);
    internal static RespValue Arr(RespValue[] items) => new(RespKind.Array, 0, null, items);

    public bool IsNull => Kind == RespKind.Null;
    public bool IsError => Kind == RespKind.Error;

    /// <summary>Raw bytes of a bulk/simple string/error; null for other kinds.</summary>
    public byte[]? Bytes => _bytes;

    /// <summary>Array elements (empty for non-arrays).</summary>
    public RespValue[] Items => _items ?? [];

    public string? AsString() => _bytes is null ? null : Encoding.UTF8.GetString(_bytes);

    public long AsInteger() => Kind switch
    {
        RespKind.Integer => Integer,
        RespKind.BulkString or RespKind.SimpleString when _bytes is not null
            && Utf8Parser.TryParse(_bytes, out long n, out _) => n,
        _ => throw new RedisException($"reply is {Kind}, not an integer"),
    };

    public double AsDouble() => Kind == RespKind.BulkString && _bytes is not null
            && Utf8Parser.TryParse(_bytes, out double d, out _)
        ? d
        : throw new RedisException($"reply is {Kind}, not a double");

    public bool AsBool() => AsInteger() != 0;

    public override string ToString() => Kind switch
    {
        RespKind.Null => "(nil)",
        RespKind.Integer => Integer.ToString(),
        RespKind.Array => $"[{string.Join(", ", Items.Select(i => i.ToString()))}]",
        _ => AsString() ?? "(nil)",
    };
}
