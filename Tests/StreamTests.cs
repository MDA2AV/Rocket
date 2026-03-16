using System.Net.Sockets;
using System.Text;
using Xunit;
using zerg;
using Zerg.Core;

namespace Tests;

public class StreamTests
{
    // ========================================================================
    // Echo handler using ConnectionStream
    // ========================================================================

    private static async Task StreamEchoHandler(Connection connection)
    {
        try
        {
            var stream = new ConnectionStream(connection);
            var buf = new byte[4096];

            while (true)
            {
                var n = await stream.ReadAsync(buf);
                if (n == 0) break;

                await stream.WriteAsync(buf.AsMemory(0, n));
                await stream.FlushAsync();
            }
        }
        catch { /* connection gone */ }
    }

    // ========================================================================
    // Tests
    // ========================================================================

    [Fact]
    public async Task Stream_Echo_SingleMessage()
    {
        await using var server = new ZergTestServer(StreamEchoHandler);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = "Hello, Stream!"u8.ToArray();
        await stream.WriteAsync(sent);

        var buf = new byte[1024];
        var n = await stream.ReadAsync(buf);
        var received = buf.AsSpan(0, n).ToArray();

        Assert.Equal(sent, received);
    }

    [Fact]
    public async Task Stream_Echo_MultipleMessages_SameConnection()
    {
        await using var server = new ZergTestServer(StreamEchoHandler);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        for (int i = 0; i < 10; i++)
        {
            var sent = Encoding.UTF8.GetBytes($"stream-msg-{i}");
            await stream.WriteAsync(sent);
            await stream.FlushAsync();

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            var received = Encoding.UTF8.GetString(buf, 0, n);

            Assert.Equal($"stream-msg-{i}", received);
        }
    }

    [Fact]
    public async Task Stream_Echo_MultipleConcurrentConnections()
    {
        await using var server = new ZergTestServer(StreamEchoHandler, reactorCount: 2);
        await Task.Delay(100);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();

            var sent = Encoding.UTF8.GetBytes($"stream-conn-{i}");
            await stream.WriteAsync(sent);

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            Assert.Equal($"stream-conn-{i}", Encoding.UTF8.GetString(buf, 0, n));
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Stream_Echo_LargePayload()
    {
        await using var server = new ZergTestServer(StreamEchoHandler);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = new byte[4096];
        Random.Shared.NextBytes(sent);
        await stream.WriteAsync(sent);

        using var ms = new MemoryStream();
        var buf = new byte[8192];
        while (ms.Length < sent.Length)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }

        Assert.Equal(sent, ms.ToArray());
    }

    [Fact]
    public async Task Stream_HandlesClientDisconnect()
    {
        var connectionClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handler(Connection connection)
        {
            try
            {
                var stream = new ConnectionStream(connection);
                var buf = new byte[1024];

                while (true)
                {
                    var n = await stream.ReadAsync(buf);
                    if (n == 0)
                    {
                        connectionClosed.TrySetResult();
                        break;
                    }
                }
            }
            catch
            {
                connectionClosed.TrySetResult();
            }
        }

        await using var server = new ZergTestServer(Handler);
        await Task.Delay(100);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();
            await stream.WriteAsync("hello"u8.ToArray());
            await Task.Delay(50);
        } // client disposed → TCP FIN

        var completed = await Task.WhenAny(connectionClosed.Task, Task.Delay(5000));
        Assert.Equal(connectionClosed.Task, completed);
    }
}
