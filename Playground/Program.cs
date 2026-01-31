using System.Runtime.CompilerServices;
using URocket.Engine;
using URocket.Engine.Configs;
using static Playground.HttpResponse;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

namespace Playground;

[SkipLocalsInit]
internal static class Program 
{
    internal static async Task Main() 
    {
        await Execute(); 
    }

    private static async Task Execute() 
    {
        var engine = new Engine(new EngineOptions
        {
            Port = 8080,
            ReactorCount = 16
        });
        engine.Listen();
        
        var cts = new CancellationTokenSource();

        _ = Task.Run(async () => 
        {
            Console.ReadLine();
            engine.Stop();
            await cts.CancelAsync();
            
        }, cts.Token);
            
        try
        {
            while (engine.ServerRunning) 
            {
                var conn = await engine.AcceptAsync(cts.Token);
                Console.WriteLine($"Connection: {conn.ClientFd}");
                
                _ = HandleConnectionStreamAsync(conn);
                //_ = HandleConnectionAsync(conn);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Signaled to stop");
        }

        Console.WriteLine("Execution finished.");
    }
}