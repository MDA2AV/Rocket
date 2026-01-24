// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

using System.Collections.Concurrent;
using System.Threading.Channels;
using URocket.Engine.Configs;

namespace URocket.Engine;

public sealed partial class Engine 
{
    /// <summary>
    /// Global ID used when registering the io_uring buffer ring (buf_ring).
    /// Must match the ID used by all reactors when arming multishot recv.
    /// </summary>
    private const int c_bufferRingGID = 1;
    /// <summary>
    /// Per-reactor connection counters used for metrics / diagnostics / load balancing.
    /// Index corresponds to reactor ID.
    /// </summary>
    private static long[] ReactorConnectionCounts = null!;
    /// <summary>
    /// Lock-free queues used by the acceptor to hand off accepted client fds
    /// to each reactor thread (one queue per reactor).
    /// </summary>
    private static ConcurrentQueue<int>[] ReactorQueues = null!; // TODO: Use Channels? // Lock-free queues for passing accepted fds to reactor
    /// <summary>
    /// Global running flag checked by acceptor and reactors to stop loops gracefully.
    /// </summary>
    public bool ServerRunning { get; private set; }
    /// <summary>
    /// The single acceptor responsible for listening and distributing connections.
    /// </summary>
    public Acceptor SingleAcceptor { get; set; } = null!;
    /// <summary>
    /// Array of reactors. Each reactor owns its own io_uring instance,
    /// connection map, and event loop.
    /// </summary>
    public Reactor[] Reactors { get; set; } = null!;
    /// <summary>
    /// Per-reactor connection dictionaries (fd -> Connection).
    /// Index corresponds to reactor ID.
    /// </summary>
    public Dictionary<int, Connection.Connection>[] Connections { get; set; } = null!;
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
        ReactorQueues = new ConcurrentQueue<int>[options.ReactorCount];
        ReactorConnectionCounts = new long[options.ReactorCount];
    }

    /// <summary>
    /// Channel used to notify the application layer that a new connection
    /// was fully registered in a reactor.
    /// </summary>
    private readonly Channel<ConnectionItem> ConnectionQueues =
        Channel.CreateUnbounded<ConnectionItem>(new UnboundedChannelOptions());
    /// <summary>
    /// Internal struct used to pass (reactorId, fd) pairs
    /// from the acceptor to the async AcceptAsync API.
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
    public async ValueTask<Connection.Connection?> AcceptAsync(CancellationToken cancellationToken = default) 
    {
        while (true) 
        {
            var item = await ConnectionQueues.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            var dict = Connections[item.ReactorId];
            if (dict.TryGetValue(item.ClientFd, out var conn))
                return conn;

            // The fd was closed/removed before we got here (recv res<=0 path).
            // Skip it and wait for the next accepted connection.
        }
    }
    /// <summary>
    /// Starts the engine:
    ///  - creates acceptor
    ///  - creates reactors
    ///  - starts reactor threads
    ///  - starts acceptor thread
    /// </summary>
    public void Listen() 
    {
        ServerRunning = true;
        // Init Acceptor
        SingleAcceptor = new Acceptor(Options.AcceptorConfig, this);
        
        // Init Reactors
        Reactors = new Reactor[Options.ReactorCount];
        Connections = new Dictionary<int, Connection.Connection>[Options.ReactorCount];
        for (var i = 0; i < Options.ReactorCount; i++) 
        {
            ReactorQueues[i] = new ConcurrentQueue<int>();
            ReactorConnectionCounts[i] = 0;
            
            Reactors[i] = new Reactor(i, Options.ReactorConfigs[i],this);
            Connections[i] = new Dictionary<int, Connection.Connection>(Reactors[i].Config.MaxConnectionsPerReactor);
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
                        Reactors[wi].HandleSubmitAndWaitSingleCall();
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

        var acceptorThread = new Thread(() => 
        {
            try
            {
                SingleAcceptor.InitRing();
                SingleAcceptor.Handle(SingleAcceptor, Options.ReactorCount);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[acceptor] crash: {ex}"); 
            }
        });
        acceptorThread.Start();
        Console.WriteLine($"Server started with {Options.ReactorCount} reactors + 1 acceptor");
    }
    /// <summary>
    /// Signals all loops (acceptor + reactors) to exit.
    /// Threads will stop once they observe ServerRunning == false.
    /// </summary>
    public void Stop() => ServerRunning = false;
}