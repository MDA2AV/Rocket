using zerg;
using zerg.core;

namespace Examples.DualPipe;

internal sealed class DualPipeExample
{
    internal static async Task HandleConnectionAsync(Connection connection)
    {
        var pipe = new ConnectionDualPipe(connection);

        while (true)
        {
            var result = await pipe.Input.ReadAsync();
            if (result.IsCompleted)
                break;

            pipe.Input.AdvanceTo(result.Buffer.End);

            var msg =
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;
            var span = pipe.Output.GetSpan(msg.Length);
            msg.CopyTo(span);
            pipe.Output.Advance(msg.Length);

            await pipe.Output.FlushAsync();
        }

        await pipe.Input.CompleteAsync();
        await pipe.Output.CompleteAsync();
    }
}
