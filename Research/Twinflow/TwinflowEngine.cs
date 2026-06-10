// ReSharper disable SuggestVarOrType_BuiltInTypes
namespace Twinflow;

/// <summary>N io_uring reactors (SO_REUSEPORT) feeding accepted connections to Kestrel via a channel.</summary>
public sealed class TwinflowEngine
{
    private readonly List<TwinflowReactor> _reactors = [];
    private readonly List<Thread> _reactorThreads = [];
    private readonly Channel<TwinflowConnection> _accepted =
        Channel.CreateUnbounded<TwinflowConnection>(new UnboundedChannelOptions
        {
            SingleReader = false, 
            SingleWriter = false
        });

    private readonly ushort _port;
    private readonly int _reactorCount;
    private readonly int _recvBufferSize;
    private readonly int _bufferRingEntries;
    private readonly uint _ringEntries;

    internal TwinflowEngine(ushort port, int reactorCount,
                            uint ringEntries = 8192, int recvBufferSize = 16 * 1024, int bufferRingEntries = 4096)
    {
        _port = port;
        _reactorCount = reactorCount;
        _ringEntries = ringEntries;
        _recvBufferSize = recvBufferSize;
        _bufferRingEntries = bufferRingEntries;
    }

    internal void Start()
    {
        for (int i = 0; i < _reactorCount; i++)
        {
            Console.WriteLine($"Starting twinflow reactor {i}");
            var reactor = new TwinflowReactor(i, _port, _ringEntries, _recvBufferSize, _bufferRingEntries)
            {
                OnAccept = OnReactorAccept
            };
            
            _reactors.Add(reactor);
            
            var t = new Thread(reactor.Run)
            {
                IsBackground = true, 
                Name = $"twinflow-r{i}"
            };
            
            _reactorThreads.Add(t);
            t.Start();
        }
    }

    private void OnReactorAccept(TwinflowConnection conn) => _accepted.Writer.TryWrite(conn);

    internal ValueTask<TwinflowConnection> AcceptAsync(CancellationToken ct) => _accepted.Reader.ReadAsync(ct);

    internal void Stop()
    {
        foreach (var r in _reactors)
        {
            r.Stop();
        }
        foreach (var t in _reactorThreads)
        {
            t.Join(TimeSpan.FromSeconds(2));
        }
        _accepted.Writer.TryComplete();
    }
}
