using dogrider.Protocol;
using dogrider.Server;

namespace dogrider.Demo;

internal static class Program
{
    private static async Task Main()
    {
        var handler = new EchoHandler();
        await using var server = new DogriderServer(
            ip: "0.0.0.0",
            port: 8080,
            reactorCount: Math.Max(1, 12),
            handler: handler);

        server.Start();
        Console.WriteLine("dogrider listening on ws://0.0.0.0:8080/");
        Console.WriteLine("Ctrl+C to stop.");

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
        await stop.Task;

        await server.StopAsync();
    }
}

internal sealed class EchoHandler : IImperativeHandler
{
    public async ValueTask HandleAsync(IImperativeConnection connection)
    {
        while (true)
        {
            var frame = await connection.ReadFrameAsync();

            if (frame.IsError(out var err))
            {
                if (err.ErrorType is FrameErrorType.ConnectionClosed)
                    return;

                // Protocol error → send Close with 1002 and stop.
                connection.WriteClose(statusCode: 1002, reason: err.Message);
                await connection.FlushAsync();
                return;
            }

            switch (frame.Type)
            {
                case FrameType.Text:
                {
                    connection.WriteText(frame.DataCopy.Span);
                    await connection.FlushAsync();
                    break;
                }

                case FrameType.Binary:
                {
                    connection.WriteBinary(frame.DataCopy.Span);
                    await connection.FlushAsync();
                    break;
                }

                case FrameType.Ping:
                {
                    connection.WritePong(frame.DataCopy.Span);
                    await connection.FlushAsync();
                    break;
                }

                case FrameType.Close:
                {
                    connection.WriteClose(1000);
                    await connection.FlushAsync();
                    return;
                }

                case FrameType.Pong:
                case FrameType.Continue:
                    // Continue frames: a real server would assemble fragmented messages.
                    // For an echo demo, ignore.
                    break;
            }
        }
    }
}
