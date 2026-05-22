using System.Threading.Channels;

namespace Raptor;

/// <summary>
/// Owns N reactors (one thread + ring + SO_REUSEPORT listener each) and funnels
/// accepted connections into a channel the Kestrel listener reads.
/// </summary>
public sealed class RaptorEngine
{
    private readonly RaptorReactor[] _reactors;
    private readonly Thread[] _threads;
    private readonly Channel<RaptorConnection> _accepted =
        Channel.CreateUnbounded<RaptorConnection>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    internal RaptorEngine(ushort port, int reactorCount, uint ringEntries, int recvBufSize, int backlog)
    {
        _reactors = new RaptorReactor[reactorCount];
        _threads  = new Thread[reactorCount];
        for (int i = 0; i < reactorCount; i++)
            _reactors[i] = new RaptorReactor(i, port, ringEntries, recvBufSize, backlog) { OnAccept = OnReactorAccept };
    }

    private void OnReactorAccept(RaptorConnection conn) => _accepted.Writer.TryWrite(conn);

    public void Start()
    {
        for (int i = 0; i < _reactors.Length; i++)
        {
            int idx = i;
            var t = new Thread(() => _reactors[idx].Run()) { IsBackground = true, Name = $"raptor-r{idx}" };
            _threads[idx] = t;
            t.Start();
        }
    }

    internal ValueTask<RaptorConnection> AcceptAsync(CancellationToken ct) => _accepted.Reader.ReadAsync(ct);

    public void Stop()
    {
        _accepted.Writer.TryComplete();
        foreach (RaptorReactor r in _reactors) r.Stop();
    }
}
