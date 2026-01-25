using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

internal static class Boot
{
    internal static long StartTs;

    [ModuleInitializer]
    internal static void Init()
        => StartTs = Stopwatch.GetTimestamp();
}

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var app = builder.Build();
        app.MapGet("/", () => "Hello");
        await app.StartAsync();
        
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        var ipAdress = IPAddress.Parse("127.0.0.1");
        await sock.ConnectAsync(new IPEndPoint(ipAdress, 5000));
        sock.Send("GET / HTTP/1.1\r\nHost: S\r\n\r\n"u8);
        var buffer = new byte[128];
        var receivedBytesCount = await sock.ReceiveAsync(buffer);
        
        var elapsed = Stopwatch.GetElapsedTime(Boot.StartTs);
        Console.WriteLine($"Process-start → first response: {elapsed.TotalMilliseconds:F3} ms");
        Console.WriteLine($"Received {receivedBytesCount} bytes");
        
        sock.Dispose();
    }
}