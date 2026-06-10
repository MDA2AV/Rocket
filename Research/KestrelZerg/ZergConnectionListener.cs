using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using zerg.Engine;

namespace KestrelZerg;

internal sealed class ZergConnectionListener : IConnectionListener
{
    private readonly Engine _engine;
    private readonly ILogger<ZergConnectionListener> _logger;
    private readonly CancellationTokenSource _stopAccepting = new();
    private long _connectionCounter;

    public ZergConnectionListener(Engine engine, IPEndPoint endpoint, ILogger<ZergConnectionListener> logger)
    {
        _engine = engine;
        EndPoint = endpoint;
        _logger = logger;
    }

    public EndPoint EndPoint { get; }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (_stopAccepting.IsCancellationRequested)
            return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopAccepting.Token);

        try
        {
            var conn = await _engine.AcceptAsync(linked.Token).ConfigureAwait(false);
            if (conn == null)
                return null;

            var id = Interlocked.Increment(ref _connectionCounter);
            return new ZergConnectionContext(conn, EndPoint, id);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        // Stop accepting new connections; in-flight connections keep running on their reactor threads.
        // (zerg has no separate "stop listener but keep reactors" knob — full shutdown happens in DisposeAsync.)
        _stopAccepting.Cancel();
        _logger.LogInformation("[zerg] Unbound listener on {Endpoint}", EndPoint);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _stopAccepting.Cancel();
        _engine.Stop();
        _logger.LogInformation("[zerg] Stopped engine on {Endpoint}", EndPoint);
        return ValueTask.CompletedTask;
    }
}
