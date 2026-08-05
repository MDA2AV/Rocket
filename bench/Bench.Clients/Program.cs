// Load driver for the ring-native HTTP clients, one protocol per run:
//
//     Bench.Clients h1|h2|h3 <host> <port> <seconds> [concurrency]
//
// BENCH_REACTORS reactors (default 4) each drive `concurrency` request loops; every await
// resumes inline on its reactor. Prints one machine-readable line:
//
//     client-h1 4r: 812345 req/s (4874070 ok, 0 failed)
using System.Diagnostics;
using ioxide;
using ioxide.httpclient;

string mode     = args[0];
string host     = args[1];
ushort port     = ushort.Parse(args[2]);
int seconds     = int.Parse(args[3]);
int concurrency = args.Length > 4 ? int.Parse(args[4]) : 64;
int reactors    = int.TryParse(Environment.GetEnvironmentVariable("BENCH_REACTORS"), out int r) ? r : 4;

long completed = 0, failed = 0;
var stop = new ManualResetEventSlim(false);
byte[] path = System.Text.Encoding.ASCII.GetBytes(Environment.GetEnvironmentVariable("BENCH_PATH") ?? "/");

var threads = new Thread[reactors];
for (int i = 0; i < reactors; i++)
{
    var config = new ServerConfig
    {
        ReactorCount = reactors,
        RecvBufferSize = 4096,
        RecvSlots = 256,
        Tcp = null,   // client-only reactors: nothing listens
        Udp = null,
    };
    var reactor = new Reactor(i, config)
    {
        OnStart = r =>
        {
            Func<ValueTask<HttpClientResponse>> send = mode switch
            {
                "h1" => Drive(HttpClientPool.Start(r, new HttpClientOptions
                    { Host = host, Port = port, PoolSize = 4 })),
                "h2" => Drive(Http2ClientPool.Start(r, new Http2ClientOptions
                    { Host = host, Port = port, PoolSize = 1 })),
                "h3" => Drive(Http3ClientPool.Start(r, new Http3ClientOptions
                    { Host = host, Port = port, ServerName = "localhost", PoolSize = 1 })),
                _ => throw new ArgumentException(mode),
            };
            for (int loop = 0; loop < concurrency; loop++)
            {
                _ = LoopAsync(send);
            }
        },
    };
    threads[i] = new Thread(reactor.Run) { IsBackground = true, Name = $"bench-{i}" };
    threads[i].Start();
}

Func<ValueTask<HttpClientResponse>> Drive(object pool) => pool switch
{
    HttpClientPool p  => () => p.GetAsync(path),
    Http2ClientPool p => () => p.GetAsync(path),
    Http3ClientPool p => () => p.GetAsync(path),
    _ => throw new UnreachableException(),
};

async Task LoopAsync(Func<ValueTask<HttpClientResponse>> send)
{
    while (!stop.IsSet)
    {
        try
        {
            using HttpClientResponse response = await send();
            if (response.Status == 200) { Interlocked.Increment(ref completed); }
            else { Interlocked.Increment(ref failed); }
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref failed) <= 3)
            {
                Console.Error.WriteLine($"FAIL: {e.GetType().Name}: {e.Message}");
            }
            await Task.Yield();
        }
    }
}

Thread.Sleep(2000);   // connect + handshake, outside the window
Interlocked.Exchange(ref completed, 0);
Interlocked.Exchange(ref failed, 0);
var watch = Stopwatch.StartNew();
Thread.Sleep(seconds * 1000);
watch.Stop();
long ok = Interlocked.Read(ref completed), bad = Interlocked.Read(ref failed);
stop.Set();
Console.WriteLine($"client-{mode} {reactors}r: {ok / watch.Elapsed.TotalSeconds:F0} req/s ({ok} ok, {bad} failed)");
Environment.Exit(bad > ok ? 1 : 0);
