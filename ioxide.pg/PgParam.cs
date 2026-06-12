using System.Buffers.Text;
using System.Text;

namespace ioxide.pg;

/// <summary>
/// A query parameter for the extended (prepared-statement) protocol. Values are sent in text
/// format - the server parses them against the parameter types it infers for the statement - so
/// the same plan is reused across calls while the wire encoding stays trivial. Build with
/// <see cref="Int"/>, <see cref="Text"/>, or <see cref="Null"/>.
/// </summary>
public readonly struct PgParam
{
    private readonly long _num;
    private readonly string? _str;
    private readonly byte _kind;   // 0 = null, 1 = integer, 2 = text

    private PgParam(byte kind, long num, string? str)
    {
        _kind = kind;
        _num = num;
        _str = str;
    }

    /// <summary>An integer parameter (int2/int4/int8, per the statement's inferred type).</summary>
    public static PgParam Int(long value) => new(1, value, null);

    /// <summary>A text parameter (text/varchar, or any type with a text input - dates, numerics, ...).</summary>
    public static PgParam Text(string value) => new(2, 0, value);

    /// <summary>A SQL NULL.</summary>
    public static readonly PgParam Null = new(0, 0, null);

    internal bool IsNull => _kind == 0;

    /// <summary>Upper bound on the text encoding's byte length, for sizing the send buffer.</summary>
    internal int MaxBytes => _kind switch
    {
        0 => 0,
        1 => 20,                                 // long: at most 19 digits plus a sign
        _ => Encoding.UTF8.GetByteCount(_str!),
    };

    /// <summary>Write the text encoding into <paramref name="destination"/>; returns bytes written. Not called for NULL.</summary>
    internal int WriteText(Span<byte> destination)
    {
        if (_kind == 1)
        {
            Utf8Formatter.TryFormat(_num, destination, out int written);
            return written;
        }
        return Encoding.UTF8.GetBytes(_str!, destination);
    }
}
