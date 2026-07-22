using System.Globalization;
using System.Text;

namespace ioxide.redis;

/// <summary>
/// A single command argument. Implicit conversions cover the common types; everything is sent as a
/// RESP bulk string, so a value's type only matters for how it's rendered to bytes.
/// </summary>
public readonly struct RedisArg
{
    internal readonly byte[] Bytes;

    private RedisArg(byte[] bytes) => Bytes = bytes;

    public static implicit operator RedisArg(string value) => new(Encoding.UTF8.GetBytes(value));
    public static implicit operator RedisArg(byte[] value) => new(value);
    public static implicit operator RedisArg(int value) => new(Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture)));
    public static implicit operator RedisArg(long value) => new(Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture)));
    public static implicit operator RedisArg(double value) => new(Encoding.ASCII.GetBytes(value.ToString("R", CultureInfo.InvariantCulture)));
    public static implicit operator RedisArg(bool value) => new(value ? "1"u8.ToArray() : "0"u8.ToArray());
}
