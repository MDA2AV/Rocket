// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace URocket.Engine;

public sealed partial class Engine {
    
    private const int c_bufferRingGID = 1;
    public int NReactors { get; set; }

    public bool ServerRunning { get; private set; }
    
    // Lock-free queues for passing accepted fds to reactors
    private static ConcurrentQueue<int>[] ReactorQueues = null!; // TODO: Use Channels?
    // Stats tracking
    private static long[] ReactorConnectionCounts = null!;
    private static long[] ReactorRequestCounts = null!;
    
    // Socket
    public string Ip { get; private set; } = "0.0.0.0";
    public ushort Port { get; private set; } = 8080;
    public int Backlog { get; private set; } = 65535;
    
    
    private readonly Channel<ConnectionItem> ConnectionQueues =
        Channel.CreateUnbounded<ConnectionItem>(new UnboundedChannelOptions());
    
    private readonly Channel<Connection> _accepted =
        Channel.CreateUnbounded<Connection>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false
        });
    
    /*
    public ValueTask<Connection> AcceptAsync(CancellationToken ct = default)
        => _accepted.Reader.ReadAsync(ct);
    */
    
    public async ValueTask<Connection> AcceptAsync2(CancellationToken cancellationToken = default) {
        var item = await ConnectionQueues.Reader.ReadAsync(cancellationToken);
        
        return Connections[item.ReactorId][item.ClientFd];
    }
    
    public async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var item = await ConnectionQueues.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            var dict = Connections[item.ReactorId];
            if (dict.TryGetValue(item.ClientFd, out var conn))
                return conn;

            // The fd was closed/removed before we got here (recv res<=0 path).
            // Just skip it and wait for the next accepted connection.
        }
    }
    
    public Engine() {
        NReactors = 16;
        ReactorQueues = new ConcurrentQueue<int>[NReactors];
        ReactorConnectionCounts = new long[NReactors];
        ReactorRequestCounts = new long[NReactors];
    }
    
    public struct ConnectionItem {
        public readonly int ReactorId;
        public readonly int ClientFd;
        public ConnectionItem(int reactorId, int clientFd) {
            ReactorId = reactorId;
            ClientFd = clientFd;
        }
    }
    
    public void Listen() {
        ServerRunning = true;
        // Init Acceptor
        SingleAcceptor = new Acceptor(this); // TODO: How to pass a config
        SingleAcceptor.InitRing();
        
        // Init Reactors
        Reactors = new Reactor[NReactors];
        Connections = new Dictionary<int, Connection>[NReactors];
        for (var i = 0; i < NReactors; i++) {
            ReactorQueues[i] = new ConcurrentQueue<int>();
            ReactorConnectionCounts[i] = 0;
            ReactorRequestCounts[i] = 0;
            
            Reactors[i] = new Reactor(i,this); // TODO: How to pass a config
            Reactors[i].InitRing();
            Connections[i] = new Dictionary<int, Connection>(Reactors[i].Config.MaxConnectionsPerReactor);
        }
        
        var reactorThreads = new Thread[NReactors];
        for (int i = 0; i < NReactors; i++) {
            int wi = i;
            reactorThreads[i] = new Thread(() => {
                try { Reactors[wi].Handle(); }
                catch (Exception ex) { Console.Error.WriteLine($"[w{wi}] crash: {ex}"); }
            })
            { IsBackground = true, Name = $"uring-w{wi}" };
            reactorThreads[i].Start();
        }

        var acceptorThread = new Thread(() => {
            try { SingleAcceptor.Handle(SingleAcceptor, NReactors); }
            catch (Exception ex) { Console.Error.WriteLine($"[acceptor] crash: {ex}"); }
        });
        acceptorThread.Start();
        Console.WriteLine($"Server started with {NReactors} reactors + 1 acceptor");
    }
    
    public void Stop() => ServerRunning = false;
}