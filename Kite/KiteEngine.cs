namespace Kite;

/// <summary>N io_uring reactors (SO_REUSEPORT) feeding accepted connections to Kestrel via a channel.</summary>
public sealed class KiteEngine
{
    private readonly List<KiteReactor> _reactors = new();
    private readonly List<Thread> _threads = new();
    private readonly Channel<KiteConnection> _accepted =
        Channel.CreateUnbounded<KiteConnection>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly ushort _port;
    private readonly int _reactorCount;
    private readonly int _recvBufferSize;
    private readonly int _bufferRingEntries;
    private readonly uint _ringEntries;

    internal KiteEngine(ushort port, int reactorCount,
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
            var r = new KiteReactor(i, _port, _ringEntries, _recvBufferSize, _bufferRingEntries) { OnAccept = OnReactorAccept };
            _reactors.Add(r);
            var t = new Thread(r.Run) { IsBackground = true, Name = $"kite-r{i}" };
            _threads.Add(t);
            t.Start();
        }
    }

    private void OnReactorAccept(KiteConnection conn) => _accepted.Writer.TryWrite(conn);

    internal ValueTask<KiteConnection> AcceptAsync(CancellationToken ct) => _accepted.Reader.ReadAsync(ct);

    internal void Stop()
    {
        foreach (var r in _reactors) r.Stop();
        _accepted.Writer.TryComplete();
    }
}
