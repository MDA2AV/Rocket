// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

using System.Buffers;
using System.Runtime.CompilerServices;
using rtr;
using rtr.Engine;
using rtr.Engine.Configs;
using zerg.core;

namespace Playground.Zerg;

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
            ReactorCount = 12
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
                //Console.WriteLine($"Connection: {conn.ClientFd}");

                _ = HandleConnectionAsync(conn);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Signaled to stop");
        }

        Console.WriteLine("Execution finished.");
    }
    
    internal static async Task HandleConnectionAsync(Connection connection)
    {
        while (true)
        {
            RingSnapshot result = await connection.ReadAsync();
            if (result.IsClosed)
                break;

            // Get all ring buffers data
            UnmanagedMemoryManager[] rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
            ReadOnlySequence<byte> sequence = rings.ToReadOnlySequence();

            // Process received data...

            // Return rings to the kernel
            foreach (UnmanagedMemoryManager ring in rings)
                connection.ReturnRing(ring.BufferId);

            // Write the response directly into the connection slab
            ReadOnlySpan<byte> msg =
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;

            connection.Write(msg);

            // New: async flush barrier (wait until fully flushed to kernel)
            await connection.FlushAsync();

            // Ready for next read cycle
            connection.ResetRead();
        }

        //Console.WriteLine("HandleConnectionAsync exited.");
    }
}