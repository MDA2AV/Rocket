using Examples.ZeroAlloc.Advanced;
using URocket.Engine;

namespace Examples;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

internal class Program
{
    public static async Task Main(string[] args)
    {
        // Similar to Sockets, create an object and initialize it
        // By default set to IPv4 TCP
        // (More examples on how to configure the engine coming up)
        var engine = new Engine
        {
            Port = 8080,
            NReactors = 12 // Single reactor, increase this number for higher throughput if needed.
        };
        engine.Listen();

        // Loop to handle new connections, fire and forget approach
        while (engine.ServerRunning)
        {
            var connection = await engine.AcceptAsync();
            //_ = new ZeroAlloc_Advanced_SingleRing_ConnectionHandler().HandleConnectionAsync(connection);
            _ = new ZeroAlloc_Advanced_MultiRings_ConnectionHandler().HandleConnectionAsync(connection);
            //_ = Rings_as_ReadOnlySequence.HandleConnectionAsync(connection);
            //_ = Rings_as_ReadOnlySpan.HandleConnectionAsync(connection);
        }
    }
}