using System.Runtime.CompilerServices;
using URocket.Engine;
using static Playground.HttpResponse;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

namespace Playground;

[SkipLocalsInit]
internal static class Program {
    internal static async Task Main() {
        InitOk();
        try { await Execute(); } finally { FreeOk(); }
    }

    private static async Task Execute() {
        var engine = new Engine();
        engine.Listen();
            
        while (engine.ServerRunning) {
            var conn = await engine.AcceptAsync();
            Console.WriteLine($"Connection: {conn.Fd}");
            _ = HandleAsync(conn);
        }
    }
}