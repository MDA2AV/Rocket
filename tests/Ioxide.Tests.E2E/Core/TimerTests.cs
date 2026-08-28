using System.Diagnostics;
using ioxide;
using ioxide.timer;
using ioxide.utils;

namespace Ioxide.Tests;

/// <summary>
/// RingTimer: the wait actually waits, it reports expiry rather than an error, a single timer is
/// reusable across requests on its connection, and two connections waiting different amounts get
/// their own deadlines rather than each other's.
/// </summary>
internal static class TimerTests
{
    // Answers after the milliseconds named in the path, holding one timer for the connection and
    // re-arming it per request - the shape a caller is meant to use.
    private static async Task Delay(Reactor r, TcpConnection conn)
    {
        var timer = new RingTimer(r);
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = Wire.ReadPath(conn, snapshot);
                int ms = int.TryParse(path.TrimStart('/'), out int parsed) ? parsed : 0;

                int result = ms > 0 ? await timer.DelayAsync(ms) : RingTimer.ETime;
                Wire.Write(conn, 200, RingTimer.Expired(result) ? ms.ToString() : $"errno {result}");
                await conn.FlushAsync();

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
    }

    public static void Register(Runner runner)
    {
        runner.Test("timer: the wait actually elapses", () =>
        {
            int port = TestServer.Start(Delay);
            var sw = Stopwatch.StartNew();
            (int status, string body) = Client.Get(port, "/50");
            sw.Stop();
            Assert.Equal(200, status);
            Assert.Equal("50", body);
            Assert.True(sw.Elapsed.TotalMilliseconds >= 50,
                $"answered in {sw.Elapsed.TotalMilliseconds:F1}ms, short of the 50ms asked for");
        });

        runner.Test("timer: expiry is reported as expiry, not an error", () =>
        {
            int port = TestServer.Start(Delay);
            (int status, string body) = Client.Get(port, "/5");
            Assert.Equal(200, status);
            // The handler writes "errno N" if the completion was anything but a clean expiry.
            Assert.Equal("5", body);
        });

        runner.Test("timer: one timer serves a connection's whole life", () =>
        {
            int port = TestServer.Start(Delay);
            var sw = Stopwatch.StartNew();
            var replies = Client.GetKeepAlive(port, "/10", 5);
            sw.Stop();
            Assert.Equal(5, replies.Count);
            foreach ((int status, string body) in replies)
            {
                Assert.Equal(200, status);
                Assert.Equal("10", body);
            }
            // Five waits of 10ms on one connection, served one after another.
            Assert.True(sw.Elapsed.TotalMilliseconds >= 50,
                $"five 10ms waits took {sw.Elapsed.TotalMilliseconds:F1}ms, so some did not happen");
        });

        runner.Test("timer: overlapping waits keep their own deadlines", () =>
        {
            int port = TestServer.Start(Delay);
            int[] delays = [80, 10, 40];
            var results = new (int Status, string Body, double Ms)[delays.Length];
            var threads = new Thread[delays.Length];

            for (int i = 0; i < delays.Length; i++)
            {
                int index = i;
                threads[i] = new Thread(() =>
                {
                    var sw = Stopwatch.StartNew();
                    (int status, string body) = Client.Get(port, $"/{delays[index]}");
                    sw.Stop();
                    results[index] = (status, body, sw.Elapsed.TotalMilliseconds);
                });
                threads[i].Start();
            }

            foreach (Thread t in threads)
            {
                t.Join();
            }

            for (int i = 0; i < delays.Length; i++)
            {
                Assert.Equal(200, results[i].Status);
                // Its own value came back, not whichever request finished parsing last.
                Assert.Equal(delays[i].ToString(), results[i].Body);
                Assert.True(results[i].Ms >= delays[i],
                    $"/{delays[i]} answered in {results[i].Ms:F1}ms");
            }
        });
    }
}
