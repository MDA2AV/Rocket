using System.Threading.Channels;

namespace KestrelShrike;

/// <summary>Owns N epoll reactors (each with its own SO_REUSEPORT listener) and funnels accepted connections to Kestrel.</summary>
internal sealed class EpollEngine
{
    private readonly EpollReactor[] _reactors;
    private readonly Channel<EpollConnection> _accepted =
        Channel.CreateUnbounded<EpollConnection>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public EpollEngine(ushort port, int reactorCount, int backlog, int maxEvents)
    {
        _reactors = new EpollReactor[reactorCount];
        for (int i = 0; i < reactorCount; i++)
            _reactors[i] = new EpollReactor(i, port, backlog, maxEvents) { OnAccept = c => _accepted.Writer.TryWrite(c) };
    }

    public void Start()
    {
        for (int i = 0; i < _reactors.Length; i++)
        {
            int idx = i;
            var t = new Thread(() => _reactors[idx].Run()) { IsBackground = true, Name = $"shrike-k-r{idx}" };
            t.Start();
        }
    }

    public ValueTask<EpollConnection> AcceptAsync(CancellationToken ct) => _accepted.Reader.ReadAsync(ct);

    public void Stop()
    {
        _accepted.Writer.TryComplete();
        foreach (EpollReactor r in _reactors) r.Stop();
    }
}
