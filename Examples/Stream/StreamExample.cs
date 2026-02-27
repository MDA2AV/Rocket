using zerg;

namespace Examples.Stream;

internal sealed class StreamExample
{
    internal static async Task HandleConnectionAsync(Connection connection)
    {
        var stream = new ConnectionStream(connection);
        var buf = new byte[4096];

        while (true)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0)
                break;
            
            await stream.WriteAsync(
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8.ToArray());
            await stream.FlushAsync();
        }
    }
}
