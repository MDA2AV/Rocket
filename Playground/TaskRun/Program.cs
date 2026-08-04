using System.Text;
using System.Text.Json;
using ioxide;
using Playground.Shared;
using Playground.Shared.Http;

// taskrun - the raw workload, but each request awaits a Task.Run JSON serialization. With the
// reactor SynchronizationContext installed the continuation comes home to the reactor; without it,
// it stays on the thread pool. The sample logs once if the post-await thread is off-reactor, which
// is the whole point of running it.

return PlaygroundHost.Run(new PlaygroundSample
{
    Name = "taskrun",
    Summary = "raw, but each request awaits a Task.Run serialization",
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(conn, new TaskRunResponder(reactor)),
});

internal readonly struct TaskRunResponder(Reactor reactor) : ITcpResponder
{
    private static int _offReactorSeen;

    public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
    {
        RequestParser.Drain(conn, snapshot);

        string json = await Task.Run(static () => JsonSerializer.Serialize("hello world"));

        if (!reactor.OnReactorThread && Interlocked.Exchange(ref _offReactorSeen, 1) == 0)
        {
            Console.WriteLine("[taskrun] continuation resumed OFF the reactor (no sync context)");
        }

        conn.Write(Responses.JsonHeader);
        conn.Write(Encoding.UTF8.GetBytes(json));
        await conn.FlushAsync();
    }
}
