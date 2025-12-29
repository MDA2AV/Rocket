using System.Collections.Concurrent;
using System.Threading.Channels;

namespace URocket.Engine;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

public sealed partial class Engine {
    public bool ServerRunning { get; private set; }

    // Lock-free queues for passing accepted fds to reactors
    private static ConcurrentQueue<int>[] ReactorQueues = null!; // TODO: Use Channels?
    // Stats tracking
    private static long[] ReactorConnectionCounts = null!;
    private static long[] ReactorRequestCounts = null!;
    public struct ConnectionItem {
        public readonly int ReactorId;
        public readonly int ClientFd;
        public ConnectionItem(int reactorId, int clientFd) {
            ReactorId = reactorId;
            ClientFd = clientFd;
        }
    }

    private static readonly Channel<ConnectionItem> ConnectionQueues =
        Channel.CreateUnbounded<ConnectionItem>(new UnboundedChannelOptions());
    
    public async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default) {
        var item = await ConnectionQueues.Reader.ReadAsync(cancellationToken);
        return Connections[item.ReactorId][item.ClientFd];
    }

    public void Listen() {
        ServerRunning = true;
        // Init Acceptor
        SingleAcceptor = new Acceptor(this); // TODO: How to pass a config
        SingleAcceptor.InitRing();
        
        // Init Reactors
        Reactors = new Reactor[_nReactors];
        Connections = new Dictionary<int, Connection>[_nReactors];
        for (var i = 0; i < _nReactors; i++) {
            ReactorQueues[i] = new ConcurrentQueue<int>();
            ReactorConnectionCounts[i] = 0;
            ReactorRequestCounts[i] = 0;
            
            Reactors[i] = new Reactor(i, this); // TODO: How to pass a config
            Reactors[i].InitRing();
            Connections[i] = new Dictionary<int, Connection>(Reactors[i].Config.MaxConnectionsPerReactor);
        }
        
        var reactorThreads = new Thread[_nReactors];
        for (int i = 0; i < _nReactors; i++) {
            int wi = i;
            reactorThreads[i] = new Thread(() => {
                    try { Reactors[wi].Handle(); }
                    catch (Exception ex) { Console.Error.WriteLine($"[w{wi}] crash: {ex}"); }
                })
                { IsBackground = true, Name = $"uring-w{wi}" };
            reactorThreads[i].Start();
        }

        var acceptorThread = new Thread(() => {
            try { SingleAcceptor.Handle(SingleAcceptor, _nReactors); }
            catch (Exception ex) { Console.Error.WriteLine($"[acceptor] crash: {ex}"); }
        });
        acceptorThread.Start();
        Console.WriteLine($"Server started with {_nReactors} reactors + 1 acceptor");
    }
    
    public void Stop() => ServerRunning = false;
}