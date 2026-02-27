using zerg;

namespace Examples.PipeReader;

internal sealed class PipeReaderExample
{
    internal static async Task HandleConnectionAsync(Connection connection)
    {
        var reader = new ConnectionPipeReader(connection);

        while (true)
        {
            var result = await reader.ReadAsync();
            if (result.IsCompleted)
                break;

            var buffer = result.Buffer;

            reader.AdvanceTo(buffer.End);
            
            var msg =
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;
            connection.Write(msg);

            await connection.FlushAsync();
        }

        reader.Complete();
    }
}
