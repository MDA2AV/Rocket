using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using URocket.Connection;
using URocket.Engine;
using URocket.Engine.Configs;
using URocket.Utils;
using URocket.Utils.UnmanagedMemoryManager;

namespace ColdBoot;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed


internal static class Boot
{
    internal static long StartTs;

    [ModuleInitializer]
    internal static void Init()
        => StartTs = Stopwatch.GetTimestamp();
}

internal class Program
{
    public static async Task Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        var engine = new Engine(new EngineOptions
        {
            Port = 8080,
            ReactorCount = 1
        });
        engine.Listen();
        _ = HandleAsync(engine, cts.Token);
        
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        var ipAdress = IPAddress.Parse("127.0.0.1");
        await sock.ConnectAsync(new IPEndPoint(ipAdress, 8080), cts.Token);
        sock.Send("GET / HTTP/1.1\r\nHost: S\r\n\r\n"u8);
        var buffer = new byte[128];
        var receivedBytesCount = await sock.ReceiveAsync(buffer);
        
        var elapsed = Stopwatch.GetElapsedTime(Boot.StartTs);
        Console.WriteLine($"Process-start → first response: {elapsed.TotalMilliseconds:F3} ms");
        Console.WriteLine($"Received {receivedBytesCount} bytes");
        
        sock.Dispose();
        engine.Stop();
        await cts.CancelAsync();
    }

    private static async Task HandleAsync(Engine engine, CancellationToken ct)
    {
        try
        {
            // Loop to handle new connections, fire and forget approach
            while (engine.ServerRunning)
            {
                var connection = await engine.AcceptAsync(ct);
                if (connection is null) continue;
                _ = HandleConnectionAsync(connection);
            }
        }
        catch (OperationCanceledException) { }
    }
    
    private static async Task HandleConnectionAsync(Connection connection)
    {
        while (true)
        {
            var result = await connection.ReadAsync();
            if (result.IsClosed)
                break;
            
            // Get all ring buffers data
            var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
            // Create a ReadOnlySequence<byte> to easily slice the data
            var sequence = rings.ToReadOnlySequence();
            
            // Process received data...
            
            // Return rings to the kernel
            foreach (var ring in rings)
                connection.ReturnRing(ring.BufferId);
            
            // Write the response
            var msg =
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;

            // Building an UnmanagedMemoryManager wrapping the msg, this step has no data allocation
            // however msg must be fixed/pinned because the engine reactor's needs to pass a byte* to liburing
            unsafe
            {
                var unmanagedMemory = new UnmanagedMemoryManager(
                    (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(msg)),
                    msg.Length,
                    false); // Setting freeable to false signaling that this unmanaged memory should not be freed because it comes from an u8 literal
                
                if (!connection.Write(new WriteItem(unmanagedMemory, connection.ClientFd)))
                    throw new InvalidOperationException("Failed to write response");
            }
            
            // Signal that written data can be flushed
            connection.Flush();
            // Signal we are ready for a new read
            connection.ResetRead();
        }
    }
}