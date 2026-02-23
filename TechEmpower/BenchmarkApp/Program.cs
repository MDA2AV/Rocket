using zerg.Engine;
using zerg.Engine.Configs;

namespace BenchmarkApp;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

internal class Program
{
    public static async Task Main(string[] args)
    {
        var engine = new Engine(new EngineOptions
        {
            Ip = "0.0.0.0",
            Port = 8080,
            ReactorCount = 12
        });
        engine.Listen();
        
        // Loop to handle new connections, fire and forget approach
        while (engine.ServerRunning)
        {
            var connection = await engine.AcceptAsync();
            if (connection is null) continue;
            _ = new ConnectionHandler().HandleConnectionAsync(connection);
        }
    }
}