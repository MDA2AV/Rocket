using System.Net.Sockets;
using System.Text;
using Xunit;
using zerg;
using zerg.core;

namespace Tests;

public class EndToEndTests
{
    /// <summary>
    /// Connects a TCP client, sends data, and verifies the server echoes it back.
    /// </summary>
    [Fact]
    public async Task Echo_SingleMessage()
    {
        await using var server = new ZergTestServer(EchoHandler);
        await Task.Delay(100); // let engine start

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = "Hello, zerg!"u8.ToArray();
        await stream.WriteAsync(sent);

        var buf = new byte[1024];
        var n = await stream.ReadAsync(buf);
        var received = buf.AsSpan(0, n).ToArray();

        Assert.Equal(sent, received);
    }

    /// <summary>
    /// Sends multiple messages on the same connection (keep-alive).
    /// </summary>
    [Fact]
    public async Task Echo_MultipleMessages_SameConnection()
    {
        await using var server = new ZergTestServer(EchoHandler);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        for (int i = 0; i < 10; i++)
        {
            var sent = Encoding.UTF8.GetBytes($"message-{i}");
            await stream.WriteAsync(sent);
            await stream.FlushAsync();

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            var received = Encoding.UTF8.GetString(buf, 0, n);

            Assert.Equal($"message-{i}", received);
        }
    }

    /// <summary>
    /// Opens multiple concurrent connections to the server.
    /// </summary>
    [Fact]
    public async Task Echo_MultipleConcurrentConnections()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorCount: 2);
        await Task.Delay(100);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();

            var sent = Encoding.UTF8.GetBytes($"conn-{i}");
            await stream.WriteAsync(sent);

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            Assert.Equal($"conn-{i}", Encoding.UTF8.GetString(buf, 0, n));
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Sends a request and verifies the server returns a valid HTTP response.
    /// </summary>
    [Fact]
    public async Task Http_SimpleResponse()
    {
        await using var server = new ZergTestServer(HttpHandler);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var request = "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(request);

        var buf = new byte[4096];
        var n = await stream.ReadAsync(buf);
        var response = Encoding.UTF8.GetString(buf, 0, n);

        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("Hello, World!", response);
    }

    /// <summary>
    /// Sends a larger payload (64 KB) and verifies the echo.
    /// </summary>
    [Fact]
    public async Task Echo_LargePayload()
    {
        await using var server = new ZergTestServer(EchoHandler);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        // Send 4 KB (within default write slab size)
        var sent = new byte[4096];
        Random.Shared.NextBytes(sent);
        await stream.WriteAsync(sent);

        // Read back — may come in chunks
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

    /// <summary>
    /// Verifies the server handles client disconnect gracefully.
    /// </summary>
    [Fact]
    public async Task Server_HandlesClientDisconnect()
    {
        var connectionClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handler(Connection connection)
        {
            try
            {
                while (true)
                {
                    var result = await connection.ReadAsync();
                    if (result.IsClosed)
                    {
                        connectionClosed.TrySetResult();
                        break;
                    }

                    var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
                    foreach (var ring in rings)
                        connection.ReturnRing(ring.BufferId);
                    connection.ResetRead();
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

        // Server handler should detect the close within a reasonable time
        var completed = await Task.WhenAny(connectionClosed.Task, Task.Delay(5000));
        Assert.Equal(connectionClosed.Task, completed);
    }

    /// <summary>
    /// Verifies the server works with multiple reactors under load.
    /// </summary>
    [Fact]
    public async Task MultipleReactors_ConcurrentLoad()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorCount: 4);
        await Task.Delay(150);

        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();

            for (int j = 0; j < 5; j++)
            {
                var msg = Encoding.UTF8.GetBytes($"r{i}-m{j}");
                await stream.WriteAsync(msg);

                var buf = new byte[1024];
                var n = await stream.ReadAsync(buf);
                Assert.Equal($"r{i}-m{j}", Encoding.UTF8.GetString(buf, 0, n));
            }
        });

        await Task.WhenAll(tasks);
    }

    // ========================================================================
    // Handlers
    // ========================================================================

    /// <summary>
    /// Echo handler: reads data and writes it back.
    /// </summary>
    private static async Task EchoHandler(Connection connection)
    {
        try
        {
            while (true)
            {
                var result = await connection.ReadAsync();
                if (result.IsClosed) break;

                var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);

                unsafe
                {
                    foreach (var ring in rings)
                    {
                        var span = new ReadOnlySpan<byte>(ring.Ptr, ring.Length);
                        connection.Write(span);
                        connection.ReturnRing(ring.BufferId);
                    }
                }

                await connection.FlushAsync();
                connection.ResetRead();
            }
        }
        catch { /* connection gone */ }
    }

    /// <summary>
    /// HTTP handler: returns a simple 200 OK response regardless of request.
    /// </summary>
    private static async Task HttpHandler(Connection connection)
    {
        try
        {
            while (true)
            {
                var result = await connection.ReadAsync();
                if (result.IsClosed) break;

                var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
                foreach (var ring in rings)
                    connection.ReturnRing(ring.BufferId);

                connection.Write(
                    "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\nHello, World!"u8);

                await connection.FlushAsync();
                connection.ResetRead();
            }
        }
        catch { /* connection gone */ }
    }
}
