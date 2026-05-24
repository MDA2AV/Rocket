using System.Net;
using System.Net.Sockets;
using System.Text.Json;

// Raw System.Net.Sockets HTTP/1.1 server — NO ASP.NET, NO Kestrel. A single async accept
// loop; each connection is handled on the thread pool via the runtime's async socket engine
// (epoll-backed on Linux). Same WORK_ITEMS knob + same object as Minima / AspBaseline.
int workItems = int.TryParse(Environment.GetEnvironmentVariable("WORK_ITEMS"), out int n) ? n : 0;
Payload largeObject = BuildPayload(Math.Max(workItems, 1));

byte[] response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8.ToArray();

using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
listener.Bind(new IPEndPoint(IPAddress.Any, 8080));
listener.Listen(512);
Console.WriteLine($"[SocketBaseline] listening on :8080 (WORK_ITEMS={workItems})");

while (true)
{
    Socket client = await listener.AcceptAsync();
    _ = HandleAsync(client);
}

async Task HandleAsync(Socket client)
{
    client.NoDelay = true;   // TCP_NODELAY
    byte[] buf = new byte[16 * 1024];
    try
    {
        while (true)
        {
            int read = await client.ReceiveAsync(buf.AsMemory(), SocketFlags.None);
            if (read <= 0) break;   // peer closed

            // Same work as Minima/AspBaseline: serialize the object on the thread pool
            // (the handler already runs there) and discard. WORK_ITEMS=0 → plain "ok".
            if (workItems > 0)
            {
                _ = JsonSerializer.SerializeToUtf8Bytes(largeObject);
            }

            int sent = 0;
            while (sent < response.Length)
            {
                sent += await client.SendAsync(response.AsMemory(sent), SocketFlags.None);
            }
        }
    }
    catch { }
    finally { client.Dispose(); }
}

static Payload BuildPayload(int count)
{
    var items = new Item[count];
    for (int i = 0; i < count; i++)
    {
        items[i] = new Item(i, $"item-{i}", i * 1.5, (i & 1) == 0, $"category-{i % 8}");
    }
    return new Payload(DateTime.UtcNow.ToString("O"), count, items);
}

internal sealed record Item(int Id, string Name, double Value, bool Active, string Category);
internal sealed record Payload(string Generated, int Count, Item[] Items);
