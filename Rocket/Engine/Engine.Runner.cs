using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Rocket.Engine;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

public sealed partial class RocketEngine {
    private static volatile bool StopAll = false;
    // Lock-free queues for passing accepted fds to reactors
    private static ConcurrentQueue<int>[] ReactorQueues = null!;
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
    public void Run() {
        Console.CancelKeyPress += (_, __) => StopAll = true;
        InitOk();

        // Create lock-free queues for fd distribution
        ReactorQueues = new ConcurrentQueue<int>[s_nReactors];
        ReactorConnectionCounts = new long[s_nReactors];
        ReactorRequestCounts = new long[s_nReactors];
        
        s_Reactors = new Reactor[s_nReactors];
        Connections = new Dictionary<int, Connection>[s_nReactors];
        
        for (var i = 0; i < s_nReactors; i++) {
            ReactorQueues[i] = new ConcurrentQueue<int>();
            ReactorConnectionCounts[i] = 0;
            ReactorRequestCounts[i] = 0;
            
            Connections[i] = new Dictionary<int, Connection>(s_maxConnectionsPerReactor);
            
            s_Reactors[i] = new Reactor(i);
            s_Reactors[i].InitPRing();
        }
        
        var reactorThreads = new Thread[s_nReactors];
        for (int i = 0; i < s_nReactors; i++) {
            int wi = i;
            reactorThreads[i] = new Thread(() => {
                    try { ReactorHandler(wi); }
                    catch (Exception ex) { Console.Error.WriteLine($"[w{wi}] crash: {ex}"); }
                })
                { IsBackground = true, Name = $"uring-w{wi}" };
            reactorThreads[i].Start();
        }
        
        Console.WriteLine($"Server started with {s_nReactors} reactors + 1 acceptor");
        
        try { AcceptorLoop(c_ip, s_port, s_nReactors); }
        catch (Exception ex) { Console.Error.WriteLine($"[acceptor] crash: {ex}"); }
        
        foreach (var t in reactorThreads) t.Join();
        FreeOk();
    }
}