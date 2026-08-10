namespace ioxide.http2;

/// <summary>
/// One HTTP/2 response: status, headers, and an in-memory body - bytes throughout, mirroring
/// <see cref="Http2Request"/>. Header names must be ASCII (they're lowercased as they're packed);
/// values are raw octets. Everything is framed into the connection's write buffer synchronously
/// when the handler returns, so the memories can be pooled, stackallocated behind, or static.
/// </summary>
public sealed class Http2Response
{
    public int Status { get; init; } = 200;

    /// <summary>Field lines to send, in order. Repeats are preserved - add one entry per cookie
    /// for set-cookie, which is the only correct way to send several.</summary>
    public KeyValueList Headers { get; } = new();

    public ReadOnlyMemory<byte> Body { get; init; }

    private static readonly byte[] ContentTypeName = "content-type"u8.ToArray();
    private static readonly byte[] TextPlainValue  = "text/plain; charset=utf-8"u8.ToArray();

    /// <summary>
    /// Convenience for text bodies: UTF-8 encodes and stamps content-type. The rest of the
    /// surface stays byte-level - build responses from bytes directly for the allocation-lean path.
    /// </summary>
    public static Http2Response Text(string body, int status = 200)
    {
        var response = new Http2Response
        {
            Status = status,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
        };

        response.Headers.Add(ContentTypeName, TextPlainValue);

        return response;
    }
}
