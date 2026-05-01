// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

using System.Threading.Channels;
using rtr.Engine.Configs;

namespace rtr.Engine;

public sealed partial class Engine
{
    /// <summary>
    /// Global ID used when registering the io_uring buffer ring (buf_ring) in non-incremental mode.
    /// </summary>
    private const int c_bufferRingGID = 1;
    /// <summary>
    /// Per-reactor connection counters used for metrics / diagnostics.
    /// Index corresponds to reactor ID.
    /// </summary>
    private static long[] ReactorConnectionCounts = null!;
    /// <summary>
    /// Global running flag checked by reactors to stop loops gracefully.
    /// </summary>
    public bool ServerRunning { get; private set; }
    /// <summary>
    /// Array of reactors. Each reactor owns its own io_uring instance,
    /// listening socket (SO_REUSEPORT), connection map, and event loop.
    /// </summary>
    public Reactor[] Reactors { get; set; } = null!;
    /// <summary>
    /// Per-reactor connection dictionaries (fd -> Connection).
    /// Index corresponds to reactor ID.
    /// </summary>
    public Dictionary<int, Connection>[] Connections { get; set; } = null!;
    /// <summary>
    /// Engine configuration (reactor count, networking options, buffer sizes, etc.).
    /// </summary>
    public EngineOptions Options { get; }

    public Engine() : this(new EngineOptions()) { }

    public Engine(EngineOptions options)
    {
        Options = options;
        if (options.ReactorConfigs == null!)
        {
            options.ReactorConfigs = new ReactorConfig[Options.ReactorCount];
            for (int i = 0; i < Options.ReactorCount; i++)
            {
                options.ReactorConfigs[i] = new ReactorConfig();
            }
        }
        ReactorConnectionCounts = new long[options.ReactorCount];
    }

    /// <summary>
    /// Channel used to notify the application layer that a new connection
    /// was fully registered in a reactor.
    /// </summary>
    private readonly Channel<ConnectionItem> ConnectionQueues =
        Channel.CreateUnbounded<ConnectionItem>(new UnboundedChannelOptions());
    /// <summary>
    /// Internal struct used to pass (reactorId, fd) pairs from a reactor's accept path
    /// to the async AcceptAsync API.
    /// </summary>
    private struct ConnectionItem(int reactorId, int clientFd)
    {
        public readonly int ReactorId = reactorId;
        public readonly int ClientFd = clientFd;
    }
    /// <summary>
    /// Asynchronously waits for the next accepted connection.
    /// Returns the fully registered Connection object.
    /// </summary>
    public async ValueTask<Connection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var item = await ConnectionQueues.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            var dict = Connections[item.ReactorId];
            if (dict.TryGetValue(item.ClientFd, out var conn))
                return conn;

            // The fd was closed/removed before we got here (recv res<=0 path).
        }
    }
    /// <summary>
    /// Starts the engine:
    ///  - creates reactors
    ///  - starts reactor threads (each reactor binds its own SO_REUSEPORT listener and arms
    ///    multishot accept on its own ring)
    /// </summary>
    public void Listen()
    {
        ServerRunning = true;

        Reactors = new Reactor[Options.ReactorCount];
        Connections = new Dictionary<int, Connection>[Options.ReactorCount];
        for (var i = 0; i < Options.ReactorCount; i++)
        {
            ReactorConnectionCounts[i] = 0;

            Reactors[i] = new Reactor(i, Options.ReactorConfigs[i], this);
            Connections[i] = new Dictionary<int, Connection>(Reactors[i].Config.MaxConnectionsPerReactor);
        }

        var reactorThreads = new Thread[Options.ReactorCount];
        for (int i = 0; i < Options.ReactorCount; i++)
        {
            int wi = i;
            reactorThreads[i] = new Thread(() =>
                {
                    try
                    {
                        Reactors[wi].InitRing();
                        if (Reactors[wi].Config.IncrementalBufferConsumption)
                            Reactors[wi].HandleIncremental();
                        else
                            Reactors[wi].Handle();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[w{wi}] crash: {ex}");
                    }
                })
            {
                IsBackground = true, Name = $"uring-w{wi}"
            };
            reactorThreads[i].Start();
        }

        Console.WriteLine($"Server started with {Options.ReactorCount} reactors (each accepts via SO_REUSEPORT + multishot)");
    }
    /// <summary>
    /// Signals all reactor loops to exit.
    /// Threads will stop once they observe ServerRunning == false.
    /// </summary>
    public void Stop() => ServerRunning = false;
}
