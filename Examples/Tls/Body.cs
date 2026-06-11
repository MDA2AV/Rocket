using System.Text;

namespace Examples.Tls;

/// <summary>A fixed medium-sized HTTP response, built once. Size via EXAMPLES_TLS_BODY (default 8 KB).</summary>
public static class Body
{
    public static readonly byte[] Response = Build();

    public static int Size { get; private set; }

    private static byte[] Build()
    {
        Size = int.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_TLS_BODY"), out int s) ? s : 8 * 1024;

        // A representative medium payload: repeated ASCII (stands in for JSON/HTML).
        var body = new byte[Size];
        ReadOnlySpan<byte> fill = "ioxide-tls-medium-payload "u8;
        for (int i = 0; i < Size; i++)
        {
            body[i] = fill[i % fill.Length];
        }

        string head = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {Size}\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);

        var response = new byte[headBytes.Length + body.Length];
        headBytes.CopyTo(response, 0);
        body.CopyTo(response, headBytes.Length);
        return response;
    }
}
