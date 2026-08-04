using ioxide;
using Playground.Shared;
using Playground.Shared.Http;

// hop - the raw workload, but every request bounces through the thread pool before it is answered.
// Exercises the off-reactor queues and the eventfd wake, and prices what leaving the reactor costs
// against the raw baseline.

int bodyBytes = Responses.FixedBodyBytesFromEnvironment();
byte[] response = Responses.BuildFixedOk(bodyBytes);

return PlaygroundHost.Run(new PlaygroundSample
{
    Name = "hop",
    Summary = "raw, but each request bounces through the thread pool",
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(conn, new HopResponder(response)),
});

internal readonly struct HopResponder(byte[] response) : ITcpResponder
{
    public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
    {
        await Task.Yield();   // continuation runs on the thread pool, off the reactor

        RequestParser.Drain(conn, snapshot);
        conn.Write(response);
        await conn.FlushAsync();
    }
}
