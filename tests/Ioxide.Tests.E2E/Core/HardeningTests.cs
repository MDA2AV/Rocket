using System.Net.Sockets;
using System.Text;
using ioxide;
using ioxide.utils;

namespace Ioxide.Tests;

/// <summary>
/// Regression tests for the hardening issues: #92 (gid-exhaustion reactor crash), #93 (-ENOBUFS
/// teardown of healthy connections, both loop modes), #94 (faulted handlers leaking connections).
/// Each of these failed (or killed the process) before its fix.
/// </summary>
internal static class HardeningTests
{
    public static void Register(Runner runner)
    {
        runner.Test("core: shared recv survives buffer-group exhaustion (#93)", () =>
        {
            const int totalBytes = 24 * 1024;   // vs an 8 x 1 KiB group: guaranteed exhaustion while held
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            (int port, _, _) = TestServer.StartConfigured(
                HoldFirstThenDrain(release, totalBytes),
                new ServerConfig
                {
                    RecvBufferSize = 1024, RecvSlots = 8,
                    Tcp = new TcpOptions
                    {
                        WriteSlabSize = 4096, PoolMax = 8, RecvQueueEntries = 64,
                    },
                });

            DriveExhaustion(port, totalBytes, release);
        });

        runner.Test("core: incremental recv survives per-conn ring exhaustion (#93)", () =>
        {
            const int totalBytes = 12 * 1024;   // vs a 4 x 1 KiB per-conn ring
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            (int port, _, _) = TestServer.StartConfigured(
                HoldFirstThenDrain(release, totalBytes),
                new ServerConfig
                {
                    Incremental = new IncrementalOptions { MaxConnections = 8, RecvSlots = 4, RecvBufferSize = 1024 },
                    Tcp = new TcpOptions
                    {
                        WriteSlabSize = 4096, PoolMax = 8, RecvQueueEntries = 64,
                    },
                });

            DriveExhaustion(port, totalBytes, release);
        }, skip: !TestServer.KernelAtLeast(6, 12));

        runner.Test("core: faulted handler releases the connection (#94)", () =>
        {
            (int port, _, _) = TestServer.StartConfigured(
                async (_, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("boom (test)");
                },
                new ServerConfig
                {
                    RecvBufferSize = 1024, RecvSlots = 64,
                    Tcp = new TcpOptions
                    {
                        WriteSlabSize = 4096, PoolMax = 8, RecvQueueEntries = 64,
                    },
                });

            Thread.Sleep(200);   // let WaitForListen's probe connection recycle before the baseline
            int before = Fds();

            for (int i = 0; i < 10; i++)
            {
                using var c = new TcpClient();
                c.Connect("127.0.0.1", port);
                c.GetStream().Write("x"u8.ToArray());
                Thread.Sleep(20);
            }   // dispose closes the client; the server must observe EOF and recycle

            Thread.Sleep(500);
            int leaked = Fds() - before;
            Assert.True(leaked <= 3, $"{leaked} fds leaked by 10 faulted handlers (CLOSE_WAIT sockets)");
        });

        runner.Test("core: incremental accept past MaxConnections sheds, reactor survives (#92)", () =>
        {
            (int port, _, _) = TestServer.StartConfigured(Handlers.Raw,
                new ServerConfig
                {
                    Incremental = new IncrementalOptions { MaxConnections = 4, RecvSlots = 4, RecvBufferSize = 1024 },
                    Tcp = new TcpOptions
                    {
                        WriteSlabSize = 4096, PoolMax = 8, RecvQueueEntries = 64,
                    },
                });

            Thread.Sleep(250);   // WaitForListen's probe connection must recycle and free its gid

            // Fill the reactor to its gid cap with live keep-alive connections.
            var held = new List<TcpClient>();
            for (int i = 0; i < 4; i++)
            {
                var c = new TcpClient();
                c.Connect("127.0.0.1", port);
                c.ReceiveTimeout = 4000;
                held.Add(c);
                GetOk(c);
            }

            // Beyond the cap: the unfixed core threw in AllocGid and the reactor died here.
            for (int i = 0; i < 3; i++)
            {
                using var extra = new TcpClient();
                extra.Connect("127.0.0.1", port);
                extra.ReceiveTimeout = 1500;
                try
                {
                    _ = extra.GetStream().Read(new byte[1], 0, 1);   // shed = immediate close
                }
                catch
                {
                    // RST/timeout are both acceptable shed observations
                }
            }

            // The reactor must still serve the connections it already owns...
            GetOk(held[0]);

            // ...and accept fresh ones once capacity frees.
            held[3].Close();
            held.RemoveAt(3);
            Thread.Sleep(300);   // recycle returns the gid

            using var fresh = new TcpClient();
            fresh.Connect("127.0.0.1", port);
            fresh.ReceiveTimeout = 4000;
            GetOk(fresh);

            foreach (TcpClient c in held)
            {
                c.Close();
            }
        }, skip: !TestServer.KernelAtLeast(6, 12));
    }

    private static int Fds() => Directory.EnumerateFileSystemEntries("/proc/self/fd").Count();

    private static void GetOk(TcpClient c)
    {
        NetworkStream s = c.GetStream();
        s.Write(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: t\r\n\r\n"));
        byte[] buf = new byte[256];
        int n = s.Read(buf, 0, buf.Length);
        Assert.True(n > 0 && Encoding.ASCII.GetString(buf, 0, n).Contains("200"),
            "keep-alive connection stopped responding");
    }

    // Reads once and holds the buffers until released, then drains until totalBytes arrived and
    // answers "done" - the starvation-then-recovery shape both #93 tests share.
    private static Func<Reactor, TcpConnection, Task> HoldFirstThenDrain(TaskCompletionSource release, int totalBytes)
        => async (_, conn) =>
        {
            try
            {
                long got = 0;
                bool held = false;
                while (true)
                {
                    RecvSnapshot snapshot = await conn.ReadAsync();
                    UnmanagedMemoryManager[] rings = conn.GetSnapshotMemories(snapshot);
                    foreach (UnmanagedMemoryManager m in rings)
                    {
                        got += m.Memory.Length;
                    }

                    if (!held && rings.Length > 0)
                    {
                        held = true;
                        await release.Task;   // hold the first buffers while the client floods
                    }
                    conn.ReturnBuffers(rings);

                    if (got >= totalBytes)
                    {
                        conn.Write("done"u8);
                        await conn.FlushAsync();
                    }
                    if (snapshot.IsClosed)
                    {
                        return;
                    }
                    conn.ResetRead();
                }
            }
            finally
            {
                conn.DecRef();
            }
        };

    private static void DriveExhaustion(int port, int totalBytes, TaskCompletionSource release)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = 8000;
        NetworkStream stream = client.GetStream();

        byte[] payload = new byte[totalBytes];
        stream.Write(payload, 0, 1024);                    // handler reads this and parks holding it
        Thread.Sleep(150);
        stream.Write(payload, 1024, totalBytes - 1024);    // floods past the buffer group
        Thread.Sleep(300);                                 // unfixed core: -ENOBUFS teardown here
        release.SetResult();                               // buffers return; recv must resume

        byte[] reply = new byte[4];
        int n = stream.Read(reply, 0, 4);
        Assert.True(n == 4 && Encoding.ASCII.GetString(reply) == "done",
            "connection died during buffer-group exhaustion (expected it to stall and resume)");
    }
}
