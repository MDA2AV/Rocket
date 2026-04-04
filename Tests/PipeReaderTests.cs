using System.Buffers;
using System.Net.Sockets;
using System.Text;
using Xunit;
using zerg;
using zerg.core;

namespace Tests;

public class PipeReaderTests
{
    // ========================================================================
    // Echo handler using ConnectionPipeReader
    // ========================================================================

    private static async Task PipeReaderEchoHandler(Connection connection)
    {
        try
        {
            var reader = new ConnectionPipeReader(connection);

            while (true)
            {
                var result = await reader.ReadAsync();
                if (result.IsCompleted)
                    break;

                var buffer = result.Buffer;

                foreach (var segment in buffer)
                    connection.Write(segment.Span);

                reader.AdvanceTo(buffer.End);
                await connection.FlushAsync();
            }

            reader.Complete();
        }
        catch { /* connection gone */ }
    }

    // ========================================================================
    // Tests
    // ========================================================================

    [Fact]
    public async Task PipeReader_Echo_SingleMessage()
    {
        await using var server = new ZergTestServer(PipeReaderEchoHandler);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = "Hello, PipeReader!"u8.ToArray();
        await stream.WriteAsync(sent);

        var buf = new byte[1024];
        var n = await stream.ReadAsync(buf);
        var received = buf.AsSpan(0, n).ToArray();

        Assert.Equal(sent, received);
    }

    [Fact]
    public async Task PipeReader_Echo_MultipleMessages_SameConnection()
    {
        await using var server = new ZergTestServer(PipeReaderEchoHandler);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        for (int i = 0; i < 10; i++)
        {
            var sent = Encoding.UTF8.GetBytes($"pipe-msg-{i}");
            await stream.WriteAsync(sent);
            await stream.FlushAsync();

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            var received = Encoding.UTF8.GetString(buf, 0, n);

            Assert.Equal($"pipe-msg-{i}", received);
        }
    }

    [Fact]
    public async Task PipeReader_Echo_MultipleConcurrentConnections()
    {
        await using var server = new ZergTestServer(PipeReaderEchoHandler, reactorCount: 2);
        await Task.Delay(100);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();

            var sent = Encoding.UTF8.GetBytes($"pipe-conn-{i}");
            await stream.WriteAsync(sent);

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            Assert.Equal($"pipe-conn-{i}", Encoding.UTF8.GetString(buf, 0, n));
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task PipeReader_Echo_LargePayload()
    {
        await using var server = new ZergTestServer(PipeReaderEchoHandler);
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
    public async Task PipeReader_HandlesClientDisconnect()
    {
        var connectionClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handler(Connection connection)
        {
            try
            {
                var reader = new ConnectionPipeReader(connection);

                while (true)
                {
                    var result = await reader.ReadAsync();
                    if (result.IsCompleted)
                    {
                        connectionClosed.TrySetResult();
                        break;
                    }

                    reader.AdvanceTo(result.Buffer.End);
                }

                reader.Complete();
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

    [Fact]
    public async Task PipeReader_PartialConsumption()
    {
        await using var server = new ZergTestServer(PartialConsumeHandler);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        // Send two lines in one write — handler consumes one line at a time
        var sent = "line1\nline2\n"u8.ToArray();
        await stream.WriteAsync(sent);

        // Should receive both lines echoed back
        using var ms = new MemoryStream();
        var buf = new byte[1024];
        while (ms.Length < sent.Length)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }

        Assert.Equal(sent, ms.ToArray());
    }

    /// <summary>
    /// Handler that consumes one newline-delimited line at a time,
    /// exercising PipeReader partial consumption.
    /// </summary>
    private static async Task PartialConsumeHandler(Connection connection)
    {
        try
        {
            var reader = new ConnectionPipeReader(connection);

            while (true)
            {
                var result = await reader.ReadAsync();
                if (result.IsCompleted) break;

                var buffer = result.Buffer;
                var consumed = buffer.Start;

                // Consume one complete line per iteration
                var reader2 = new SequenceReader<byte>(buffer);
                if (reader2.TryReadTo(out ReadOnlySequence<byte> _, (byte)'\n', advancePastDelimiter: false))
                {
                    // Include the \n
                    var line = buffer.Slice(0, reader2.Consumed + 1);
                    foreach (var seg in line)
                        connection.Write(seg.Span);

                    consumed = line.End;
                    await connection.FlushAsync();
                }

                reader.AdvanceTo(consumed, buffer.End);
            }

            reader.Complete();
        }
        catch { /* connection gone */ }
    }
}
