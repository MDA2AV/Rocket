using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace SocketColdBoot;

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
    private static Socket _socket;
    private static ReadOnlyMemory<byte> _data;
    
    public static async Task Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        _data = "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: keep-alive\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8.ToArray();
        
        // Dual-mode (IPv4 + IPv6) listener
        _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _socket.NoDelay = true;

        // For an IPv6 dual-mode socket, bind to IPv6Any
        _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 8080));
        _socket.Listen(1024 * 16);

        _ = HandleAsync();
        
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
        await cts.CancelAsync();
    }

    private static async Task HandleAsync()
    {
        while (true)
        {
            var client = await _socket.AcceptAsync();
            client.NoDelay = true;

            // fire-and-forget per connection
            _ = HandleSocketAsync(client);
        }
    }
    
    private static async ValueTask HandleSocketAsync(Socket client)
    {
        byte[] buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var recv = await client.ReceiveAsync(buffer);
                
                await client.SendAsync(_data);
            }
        }
        finally
        {
            client.Dispose();
        }
    }
}