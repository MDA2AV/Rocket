using System.Text;
using ioxide;

namespace Ioxide.Tests;

/// <summary>
/// Chaos aimed squarely at the io_uring reactor core - the project's keystone and the one layer with
/// no library behind it. Malformed input is only the vehicle; the target is the reactor's own
/// bookkeeping: that torn-down connections are recycled and leak no descriptors, that a pooled
/// connection carries nothing into its next life, that the write-overflow path survives a large
/// response, and that the same invariants hold on the per-connection incremental ring, a wholly
/// separate recv path.
/// </summary>
internal static class ReactorChaosTests
{
    public static void Register(Runner runner)
    {
        runner.Test("reactor: a chaos storm leaks no file descriptors", () =>
        {
            int port = ChaosServer.Start();
            ChaosClient.AssertHealthy(port);   // warm the pool and settle before the baseline

            int before = FdCount.Stable();

            byte[] garbage = new byte[4096];
            new Random(5).NextBytes(garbage);
            byte[] oversize = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n" + new string('a', 96 * 1024));

            for (int i = 0; i < 25; i++)
            {
                ChaosClient.SendThenClose(port, garbage);                                    // garbage + FIN
                ChaosClient.Reset(port, preface: "GET / HTTP/1.1\r\nHost: ch"u8.ToArray());  // RST mid-request
                ChaosClient.Responses(port, oversize, settleMs: 80);                         // oversize, refused + closed
            }

            ChaosClient.AssertHealthy(port);

            int leaked = FdCount.Stable() - before;
            Assert.True(leaked <= 3,
                $"{leaked} fds leaked after a chaos storm - the reactor must recycle every torn-down connection");
        });

        runner.Test("reactor: a pooled connection carries no state into its next life", () =>
        {
            int port = ChaosServer.Start();

            // Alternate a connection that leaves partial, never-terminated bytes in the recv/slab
            // state (then closes, returning its pooled connection object) with a clean request. If
            // recycling leaked a predecessor's bytes, the clean request would see a corrupted
            // response instead of exactly "ok".
            for (int i = 0; i < 40; i++)
            {
                ChaosClient.SendThenClose(port, Encoding.ASCII.GetBytes($"GET /junk-{i} HTTP/1.1\r\nX-partial: "));
                (int status, string body) = Client.Get(port, "/");
                Assert.Equal(200, status);
                Assert.Equal("ok", body);
            }
        });

        runner.Test("reactor: a large response survives the write-overflow path", () =>
        {
            // 40 KiB is well past the 16 KiB write slab the test config uses, so the response spills
            // into the overflow allocation and returns through a different send path than a small
            // reply - it must arrive whole and uncorrupted.
            const int bodyBytes = 40 * 1024;
            int port = ChaosServer.StartBig(bodyBytes);

            (int status, string body) = Client.Get(port, "/");
            Assert.Equal(200, status);
            Assert.Equal(bodyBytes, body.Length);
            Assert.True(body.All(c => c == 'x'), "large response corrupted through the overflow path");
        });

        runner.Test("reactor: a large response survives the segmented overflow path", () =>
        {
            // The other overflow strategy: rather than reallocating one slab (Grow), spill the excess
            // into a chain of pooled segments gathered into one vectored SENDMSG - a distinct send
            // path (BuildIovec / AdvanceIov on a partial send). Same correctness bar: arrives whole.
            const int bodyBytes = 40 * 1024;
            var config = new ServerConfig
            {
                RecvBufferSize = 4096,
                RecvSlots = 256,
                Tcp = new TcpOptions
                {
                    WriteSlabSize = 16 * 1024,
                    PoolMax = 64,
                    RecvQueueEntries = 64,
                    WriteOverflow = WriteOverflowStrategy.Segmented,
                },
            };
            int port = TestServer.StartConfigured(ChaosServer.Big(bodyBytes), config).Port;

            (int status, string body) = Client.Get(port, "/");
            Assert.Equal(200, status);
            Assert.Equal(bodyBytes, body.Length);
            Assert.True(body.All(c => c == 'x'), "large response corrupted through the segmented overflow path");
        });

        runner.Test("reactor: fragmentation and refusal hold on the incremental ring", () =>
        {
            // The per-connection IOU_PBUF_RING_INC path (OnTcpRecvCompletionIncremental) is separate
            // code from the shared buffer-ring recv, with its own offset/refcount accounting - the
            // same chaos must resolve the same way on it.
            var config = new ServerConfig
            {
                Incremental = new IncrementalOptions { MaxConnections = 64, RecvSlots = 16, RecvBufferSize = 4096 },
                Tcp = new TcpOptions { WriteSlabSize = 16 * 1024, PoolMax = 64, RecvQueueEntries = 64 },
            };
            int port = TestServer.StartConfigured(ChaosServer.Http, config).Port;

            Assert.Equal(1, ChaosClient.ResponsesFragmented(port, ChaosClient.Request(), chunk: 1, pauseMs: 2));

            byte[] oversize = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n" + new string('a', 96 * 1024));
            Assert.Equal(0, ChaosClient.Responses(port, oversize));

            ChaosClient.AssertHealthy(port);
        }, skip: !TestServer.KernelAtLeast(6, 12));
    }
}
