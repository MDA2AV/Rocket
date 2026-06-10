using System.Collections.Concurrent;
using System.Threading.Channels;
using terraform.Engine.Configs;

namespace terraform.Engine;

public sealed partial class Engine
{
    private const ushort c_bufferRingGID = 1;
    private static long[] ReactorConnectionCounts = null!;
    private static ConcurrentQueue<int>[] ReactorQueues = null!;

    public bool ServerRunning { get; private set; }
    public Acceptor SingleAcceptor { get; set; } = null!;
    public Reactor[] Reactors { get; set; } = null!;
    public Dictionary<int, Connection>[] Connections { get; set; } = null!;
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

    private readonly Channel<ConnectionItem> ConnectionQueues =
        Channel.CreateUnbounded<ConnectionItem>(new UnboundedChannelOptions());

    private struct ConnectionItem(int reactorId, int clientFd)
    {
        public readonly int ReactorId = reactorId;
        public readonly int ClientFd = clientFd;
    }

    public async ValueTask<Connection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var item = await ConnectionQueues.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            var dict = Connections[item.ReactorId];
            if (dict.TryGetValue(item.ClientFd, out var conn))
                return conn;
        }
    }

    public void Listen()
    {
        ServerRunning = true;
        SingleAcceptor = new Acceptor(Options.AcceptorConfig, this);

        Reactors = new Reactor[Options.ReactorCount];
        Connections = new Dictionary<int, Connection>[Options.ReactorCount];
        for (var i = 0; i < Options.ReactorCount; i++)
        {
            ReactorQueues[i] = new ConcurrentQueue<int>();
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

    public void Stop() => ServerRunning = false;
}
