namespace KestrelMinima;

/// <summary>
/// Owns N io_uring reactors (each its own SO_REUSEPORT listener) and funnels
/// accepted connections to the Kestrel transport via a channel.
/// </summary>
public sealed class KestrelMinimaEngine
{
    private readonly Reactor[] _reactors;
    private readonly Channel<Connection> _accepted =
        Channel.CreateUnbounded<Connection>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    public KestrelMinimaEngine(ServerConfig config)
    {
        _reactors = new Reactor[config.ReactorCount];
        for (int i = 0; i < config.ReactorCount; i++)
        {
            _reactors[i] = new Reactor(i, config) { OnAccept = OnReactorAccept };
        }
    }

    private void OnReactorAccept(Connection conn) => _accepted.Writer.TryWrite(conn);

    public void Start()
    {
        for (int i = 0; i < _reactors.Length; i++)
        {
            int idx = i;
            var t = new Thread(() => _reactors[idx].Run())
            {
                IsBackground = true,
                Name = $"kestrel-minima-r{idx}",
            };
            t.Start();
        }
    }

    public ValueTask<Connection> AcceptAsync(CancellationToken ct) => _accepted.Reader.ReadAsync(ct);

    public void Stop() => _accepted.Writer.TryComplete();
}
