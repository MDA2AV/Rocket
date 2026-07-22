using System.Buffers.Text;
using System.Text;

namespace ioxide.redis;

public enum RespKind { Null, SimpleString, Error, Integer, BulkString, Array }

/// <summary>
/// A parsed RESP2 reply. A <b>value type</b>, so parsing a reply allocates no per-node object:
/// integers, nulls and booleans are entirely allocation-free; bulk/simple strings and arrays carry
/// the bytes (or element array) materialized during parse, which outlive the receive buffer.
/// Accessors convert on demand. <see cref="IsNull"/> covers both null bulk strings (<c>$-1</c>) and
/// null arrays (<c>*-1</c>). (Layout follows StackExchange.Redis's allocation-light RedisValue.)
/// </summary>
public readonly struct RespValue
{
    /// <summary>The RESP null (a null bulk string or array); equivalent to <c>default</c>.</summary>
    public static readonly RespValue Null = default;

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
            && Utf8Parser.TryParse(_bytes, out long n, out int c) && c == _bytes.Length => n,
        _ => throw new RedisException($"reply ({Kind}) is not an integer"),
    };

    /// <summary>
    /// A numeric reply as a double. Accepts integers, and the bulk/simple-string forms Redis uses for
    /// scores - including the non-finite tokens <c>inf</c>/<c>+inf</c>/<c>-inf</c>/<c>nan</c> that
    /// <see cref="Utf8Parser"/> won't parse (e.g. a ZSCORE of +inf).
    /// </summary>
    public double AsDouble()
    {
        if (Kind == RespKind.Integer)
        {
            return Integer;
        }
        if (_bytes is not null && (Kind == RespKind.BulkString || Kind == RespKind.SimpleString))
        {
            if (Utf8Parser.TryParse(_bytes, out double d, out int n) && n == _bytes.Length)
            {
                return d;
            }
            ReadOnlySpan<byte> b = _bytes;
            if (b.SequenceEqual("inf"u8) || b.SequenceEqual("+inf"u8)) return double.PositiveInfinity;
            if (b.SequenceEqual("-inf"u8)) return double.NegativeInfinity;
            if (b.SequenceEqual("nan"u8)) return double.NaN;
        }
        throw new RedisException($"reply ({Kind}) is not a double");
    }

    public bool AsBool() => AsInteger() != 0;

    public override string ToString() => Kind switch
    {
        RespKind.Null => "(nil)",
        RespKind.Integer => Integer.ToString(),
        RespKind.Array => $"[{string.Join(", ", Items.Select(i => i.ToString()))}]",
        _ => AsString() ?? "(nil)",
    };
}
