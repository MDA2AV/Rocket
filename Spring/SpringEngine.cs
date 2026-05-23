using System.Threading.Channels;

namespace Spring;

/// <summary>
/// Owns N io_uring reactors (each its own SO_REUSEPORT listener) and funnels
/// accepted connections to the Kestrel transport via a channel.
/// </summary>
public sealed class SpringEngine
{
    private readonly Reactor[] _reactors;
    private readonly Channel<Connection> _accepted =
        Channel.CreateUnbounded<Connection>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public SpringEngine(ServerConfig config)
    {
        _reactors = new Reactor[config.ReactorCount];
        for (int i = 0; i < config.ReactorCount; i++)
            _reactors[i] = new Reactor(i, config) { OnAccept = OnReactorAccept };
    }

    private void OnReactorAccept(Connection conn)
    {
        // On the reactor thread, before the connection is used: flush continuations
        // must run off-reactor (Kestrel pool), not inline on the reactor.
        conn.UseAsyncContinuations();
        _accepted.Writer.TryWrite(conn);
    }

    public void Start()
    {
        for (int i = 0; i < _reactors.Length; i++)
        {
            int idx = i;
            var t = new Thread(() => _reactors[idx].Run()) { IsBackground = true, Name = $"spring-r{idx}" };
            t.Start();
        }
    }

    public ValueTask<Connection> AcceptAsync(CancellationToken ct) => _accepted.Reader.ReadAsync(ct);

    public void Stop() => _accepted.Writer.TryComplete();
}
