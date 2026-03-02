using System.Buffers;
using System.Net.Sockets;
using System.Text;
using Xunit;
using zerg;
using zerg.Engine.Configs;
using zerg.Utils.UnmanagedMemoryManager;

namespace Tests;

/// <summary>
/// Runs E2E tests with IncrementalBufferConsumption enabled.
/// These mirror the core tests from EndToEndTests, StreamTests, and PipeReaderTests
/// to verify the feature works transparently across all APIs.
/// </summary>
public class IncrementalBufferTests
{
    private static readonly ReactorConfig IncrementalConfig = new(
        IncrementalBufferConsumption: true
    );

    // ========================================================================
    // Low-level API (ReadAsync + RingItem)
    // ========================================================================

    [Fact]
    public async Task Incremental_Echo_SingleMessage()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = "Hello, incremental!"u8.ToArray();
        await stream.WriteAsync(sent);

        var buf = new byte[1024];
        var n = await stream.ReadAsync(buf);

        Assert.Equal(sent, buf.AsSpan(0, n).ToArray());
    }

    [Fact]
    public async Task Incremental_Echo_MultipleMessages()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        for (int i = 0; i < 10; i++)
        {
            var sent = Encoding.UTF8.GetBytes($"inc-msg-{i}");
            await stream.WriteAsync(sent);
            await stream.FlushAsync();

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            Assert.Equal($"inc-msg-{i}", Encoding.UTF8.GetString(buf, 0, n));
        }
    }

    [Fact]
    public async Task Incremental_Echo_ConcurrentConnections()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorCount: 2, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();

            var sent = Encoding.UTF8.GetBytes($"inc-conn-{i}");
            await stream.WriteAsync(sent);

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            Assert.Equal($"inc-conn-{i}", Encoding.UTF8.GetString(buf, 0, n));
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Incremental_Echo_LargePayload()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: IncrementalConfig);
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

    // ========================================================================
    // ConnectionStream API
    // ========================================================================

    [Fact]
    public async Task Incremental_Stream_Echo()
    {
        await using var server = new ZergTestServer(StreamEchoHandler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = "Hello, incremental stream!"u8.ToArray();
        await stream.WriteAsync(sent);

        var buf = new byte[1024];
        var n = await stream.ReadAsync(buf);

        Assert.Equal(sent, buf.AsSpan(0, n).ToArray());
    }

    // ========================================================================
    // ConnectionPipeReader API
    // ========================================================================

    [Fact]
    public async Task Incremental_PipeReader_Echo()
    {
        await using var server = new ZergTestServer(PipeReaderEchoHandler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = "Hello, incremental pipe!"u8.ToArray();
        await stream.WriteAsync(sent);

        var buf = new byte[1024];
        var n = await stream.ReadAsync(buf);

        Assert.Equal(sent, buf.AsSpan(0, n).ToArray());
    }

    // ========================================================================
    // Client disconnect
    // ========================================================================

    [Fact]
    public async Task Incremental_HandlesClientDisconnect()
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

        await using var server = new ZergTestServer(Handler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();
            await stream.WriteAsync("hello"u8.ToArray());
            await Task.Delay(50);
        }

        var completed = await Task.WhenAny(connectionClosed.Task, Task.Delay(5000));
        Assert.Equal(connectionClosed.Task, completed);
    }

    // ========================================================================
    // Multi-reactor load
    // ========================================================================

    [Fact]
    public async Task Incremental_MultipleReactors_ConcurrentLoad()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorCount: 4, reactorConfig: IncrementalConfig);
        await Task.Delay(150);

        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();

            for (int j = 0; j < 5; j++)
            {
                var msg = Encoding.UTF8.GetBytes($"inc-r{i}-m{j}");
                await stream.WriteAsync(msg);

                var buf = new byte[1024];
                var n = await stream.ReadAsync(buf);
                Assert.Equal($"inc-r{i}-m{j}", Encoding.UTF8.GetString(buf, 0, n));
            }
        });

        await Task.WhenAll(tasks);
    }

    // ========================================================================
    // Buffer ID reuse detection (best-effort)
    // ========================================================================

    /// <summary>
    /// Sends a 16 KB payload split into 1000 tiny fragments with minimal delays,
    /// forcing many recv CQEs on the same connection. The handler tracks each
    /// RingItem's BufferId to measure how aggressively the kernel reuses buffers
    /// via incremental consumption.
    ///
    /// Best-effort: on kernels &lt; 6.12 or when loopback coalesces fragments, all
    /// data still arrives correctly — we just won't observe bid reuse. Data correctness
    /// is always verified; bid reuse is logged but never causes a failure.
    /// </summary>
    [Fact]
    public async Task Incremental_DetectBufferReuse_BestEffort()
    {
        // 16 KB random payload split into ~1000 fragments (~17 bytes each)
        // with 1ms delay between fragments to prevent TCP coalescing
        const int payloadSize = 16 * 1024;
        const int fragmentCount = 1000;
        const int delayMs = 1;
        var fullPayload = new byte[payloadSize];
        Random.Shared.NextBytes(fullPayload);
        int fragmentSize = (payloadSize + fragmentCount - 1) / fragmentCount;

        // Run with incremental disabled first, then enabled, to compare
        var disabledConfig = new ReactorConfig(IncrementalBufferConsumption: false);
        var (disabledRings, disabledConsecutive) = await RunFragmentedSend(fullPayload, fragmentSize, delayMs, disabledConfig);

        var (enabledRings, enabledConsecutive) = await RunFragmentedSend(fullPayload, fragmentSize, delayMs, IncrementalConfig);

        // Log comparison
        Console.WriteLine(
            $"[Incremental Buffer Reuse] payload={payloadSize} fragmentSize={fragmentSize} " +
            $"fragments={((payloadSize + fragmentSize - 1) / fragmentSize)}");
        Console.WriteLine(
            $"  DISABLED: rings={disabledRings} consecutiveSameBid={disabledConsecutive}");
        Console.WriteLine(
            $"  ENABLED:  rings={enabledRings} consecutiveSameBid={enabledConsecutive}");

        // With incremental enabled, the kernel should pack multiple recvs into the
        // same buffer, producing consecutive CQEs with the same buffer ID.
        Assert.True(enabledConsecutive > disabledConsecutive,
            $"Expected more consecutive same-bid CQEs with incremental enabled ({enabledConsecutive}) " +
            $"than disabled ({disabledConsecutive})");
        Assert.True(enabledConsecutive > 0,
            "Expected at least some consecutive same-bid CQEs with incremental enabled");
    }

    private static async Task<(int Rings, int ConsecutiveSameBid)> RunFragmentedSend(
        byte[] fullPayload, int fragmentSize, int delayMs, ReactorConfig config)
    {
        var statsReceived = new TaskCompletionSource<(byte[] Data, int TotalRings, int ConsecutiveSameBid)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handler(Connection connection)
        {
            try
            {
                var receivedData = new MemoryStream();
                int totalRings = 0;
                int consecutiveSameBid = 0;
                ushort lastBid = ushort.MaxValue;

                while (receivedData.Length < fullPayload.Length)
                {
                    var result = await connection.ReadAsync();
                    if (result.IsClosed) break;

                    unsafe
                    {
                        while (connection.TryGetRing(result.TailSnapshot, out var ring))
                        {
                            totalRings++;
                            if (ring.BufferId == lastBid)
                                consecutiveSameBid++;
                            lastBid = ring.BufferId;
                            var span = new ReadOnlySpan<byte>(ring.Ptr, ring.Length);
                            receivedData.Write(span);
                            connection.ReturnRing(ring.BufferId);
                        }
                    }

                    connection.ResetRead();
                }

                statsReceived.TrySetResult((receivedData.ToArray(), totalRings, consecutiveSameBid));
            }
            catch (Exception ex)
            {
                statsReceived.TrySetException(ex);
            }
        }

        await using var server = new ZergTestServer(Handler, reactorConfig: config);
        await Task.Delay(100);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        for (int offset = 0; offset < fullPayload.Length; offset += fragmentSize)
        {
            int len = Math.Min(fragmentSize, fullPayload.Length - offset);
            await stream.WriteAsync(fullPayload.AsMemory(offset, len));
            await stream.FlushAsync();
            if (delayMs > 0)
                await Task.Delay(delayMs);
        }

        // Wait for handler stats
        var completed = await Task.WhenAny(statsReceived.Task, Task.Delay(30000));
        Assert.Equal(statsReceived.Task, completed);

        var (data, rings, consecutiveSameBid) = await statsReceived.Task;

        // Data correctness
        Assert.Equal(fullPayload, data);

        return (rings, consecutiveSameBid);
    }

    // ========================================================================
    // Small recv buffer — forces buffer-boundary splitting
    // ========================================================================

    // 64-byte recv buffers with only 4 ring entries — very tight recycling
    private static readonly ReactorConfig SmallBufferConfig = new(
        IncrementalBufferConsumption: true,
        RecvBufferSize: 64,
        ConnectionBufferRingEntries: 4
    );

    // 64-byte recv buffers with enough entries to avoid ENOBUFS on larger payloads
    private static readonly ReactorConfig SmallBufferWideConfig = new(
        IncrementalBufferConsumption: true,
        RecvBufferSize: 64,
        ConnectionBufferRingEntries: 128
    );

    [Fact]
    public async Task Incremental_SmallBuffer_Echo_SingleMessage()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: SmallBufferConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        // 200 bytes > 64-byte buffer — must span multiple buffers
        var sent = new byte[200];
        Random.Shared.NextBytes(sent);
        await stream.WriteAsync(sent);

        using var ms = new MemoryStream();
        var buf = new byte[4096];
        while (ms.Length < sent.Length)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }

        Assert.Equal(sent, ms.ToArray());
    }

    [Fact]
    public async Task Incremental_SmallBuffer_Echo_MultipleMessages()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: SmallBufferConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        for (int i = 0; i < 20; i++)
        {
            // Each message is ~100 bytes — bigger than the 64-byte recv buffer
            var sent = Encoding.UTF8.GetBytes($"smallbuf-msg-{i:D3}-padding-" + new string('X', 60));
            await stream.WriteAsync(sent);
            await stream.FlushAsync();

            using var ms = new MemoryStream();
            var buf = new byte[4096];
            while (ms.Length < sent.Length)
            {
                var n = await stream.ReadAsync(buf);
                if (n == 0) break;
                ms.Write(buf, 0, n);
            }

            Assert.Equal(sent, ms.ToArray());
        }
    }

    [Fact]
    public async Task Incremental_SmallBuffer_Echo_LargePayload()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: SmallBufferWideConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        // 4KB payload split across 64-byte buffers = ~64 buffer fills
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
    public async Task Incremental_SmallBuffer_ConcurrentConnections()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorCount: 2, reactorConfig: SmallBufferWideConfig);
        await Task.Delay(100);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();

            // 200 bytes per message, 5 messages per connection
            for (int j = 0; j < 5; j++)
            {
                var sent = new byte[200];
                Random.Shared.NextBytes(sent);
                await stream.WriteAsync(sent);
                await stream.FlushAsync();

                using var ms = new MemoryStream();
                var buf = new byte[4096];
                while (ms.Length < sent.Length)
                {
                    var n = await stream.ReadAsync(buf);
                    if (n == 0) break;
                    ms.Write(buf, 0, n);
                }

                Assert.Equal(sent, ms.ToArray());
            }
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Incremental_SmallBuffer_FragmentedSend()
    {
        // Send data in tiny fragments with delays to force many CQEs
        // and buffer ring recycling
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: SmallBufferWideConfig);
        await Task.Delay(100);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = new byte[512];
        Random.Shared.NextBytes(sent);

        // Send in 16-byte fragments with small delays
        for (int offset = 0; offset < sent.Length; offset += 16)
        {
            int len = Math.Min(16, sent.Length - offset);
            await stream.WriteAsync(sent.AsMemory(offset, len));
            await stream.FlushAsync();
            await Task.Delay(1);
        }

        using var ms = new MemoryStream();
        var buf = new byte[4096];
        while (ms.Length < sent.Length)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }

        Assert.Equal(sent, ms.ToArray());
    }

    [Fact]
    public async Task Incremental_SmallBuffer_Stream_Echo()
    {
        await using var server = new ZergTestServer(StreamEchoHandler, reactorConfig: SmallBufferConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = new byte[200];
        Random.Shared.NextBytes(sent);
        await stream.WriteAsync(sent);

        using var ms = new MemoryStream();
        var buf = new byte[4096];
        while (ms.Length < sent.Length)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }

        Assert.Equal(sent, ms.ToArray());
    }

    [Fact]
    public async Task Incremental_SmallBuffer_PipeReader_Echo()
    {
        await using var server = new ZergTestServer(PipeReaderEchoHandler, reactorConfig: SmallBufferConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = new byte[200];
        Random.Shared.NextBytes(sent);
        await stream.WriteAsync(sent);

        using var ms = new MemoryStream();
        var buf = new byte[4096];
        while (ms.Length < sent.Length)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }

        Assert.Equal(sent, ms.ToArray());
    }

    // ========================================================================
    // Handlers
    // ========================================================================

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

    private static async Task PipeReaderEchoHandler(Connection connection)
    {
        try
        {
            var reader = new ConnectionPipeReader(connection);

            while (true)
            {
                var result = await reader.ReadAsync();
                if (result.IsCompleted) break;

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
}
