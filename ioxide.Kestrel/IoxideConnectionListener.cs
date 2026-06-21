using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using ioxide;

namespace ioxide.Kestrel;

/// <summary>
/// Runs a fleet of ioxide reactors (one io_uring ring + thread each, SO_REUSEPORT load-balanced) and
/// bridges ioxide's push model (per-connection <c>Handle</c> callback on the reactor thread) to Kestrel's
/// pull model (<see cref="AcceptAsync"/>). The Handle callback wraps each connection, pushes it onto a
/// channel for Kestrel to dequeue, starts the transport pumps, and then parks until Kestrel disposes the
/// connection — keeping the connection's handler-side ref alive for its whole Kestrel lifetime.
/// </summary>
internal sealed class IoxideConnectionListener : IConnectionListener
{
    private readonly ILogger<IoxideConnectionListener> _logger;
    private readonly Channel<ConnectionContext> _accepted;
    private readonly Reactor[] _reactors;
    private readonly Thread[] _threads;
    private long _connectionCounter;
    private int _stopped;

    public EndPoint EndPoint { get; }

    public IoxideConnectionListener(IPEndPoint endpoint, IoxideTransportOptions options, ILogger<IoxideConnectionListener> logger)
    {
        EndPoint = endpoint;
        _logger = logger;

        _accepted = Channel.CreateUnbounded<ConnectionContext>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var reactorCount = Math.Max(1, options.ReactorCount);

        var cfg = new ServerConfig { ReactorCount = reactorCount, Port = (ushort)endpoint.Port };
        if (options.ConfigureServer is not null)
        {
            cfg = options.ConfigureServer(cfg);
        }
        cfg = cfg with { Port = (ushort)endpoint.Port, ReactorCount = reactorCount };

        _reactors = new Reactor[reactorCount];
        _threads = new Thread[reactorCount];

        for (var i = 0; i < reactorCount; i++)
        {
            var reactor = new Reactor(i, cfg)
            {
                Handle = HandleConnectionAsync,
            };
            _reactors[i] = reactor;
            _threads[i] = new Thread(reactor.Run)
            {
                Name = $"ioxide-reactor-{i}",
                IsBackground = true,
            };
        }

        foreach (var thread in _threads)
        {
            thread.Start();
        }

        _logger.LogInformation("[ioxide] Bound {Endpoint} with {ReactorCount} reactor(s)", endpoint, reactorCount);
    }

    // Runs on the reactor thread, fire-and-forget, once per accepted connection.
    private async Task HandleConnectionAsync(Reactor reactor, Connection conn)
    {
        var id = Interlocked.Increment(ref _connectionCounter);
        var ctx = new IoxideConnectionContext(conn, reactor, EndPoint, id);

        if (!_accepted.Writer.TryWrite(ctx))
        {
            // Listener is shutting down: nobody will dequeue this. Release immediately.
            await ctx.DisposeAsync().ConfigureAwait(false);
            conn.DecRef();
            return;
        }

        // Launch the recv/send pumps on this reactor thread (we're inside the Handle callback).
        ctx.StartPumps();

        // Park until Kestrel disposes the connection, then release the handler-side ref (-> recycle).
        await ctx.Completion.ConfigureAwait(false);
        conn.DecRef();
    }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _accepted.Writer.TryComplete();
            _logger.LogInformation("[ioxide] Unbound listener on {Endpoint}", EndPoint);
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _accepted.Writer.TryComplete();

        await Task.Run(() =>
        {
            foreach (var reactor in _reactors)
            {
                reactor.Stop();
            }
            foreach (var thread in _threads)
            {
                thread.Join(TimeSpan.FromSeconds(5));
            }
        }).ConfigureAwait(false);

        _logger.LogInformation("[ioxide] Stopped {ReactorCount} reactor(s) on {Endpoint}", _reactors.Length, EndPoint);
    }
}
