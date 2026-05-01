using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using zerg;
using zerg.core;

namespace KestrelZerg;

/// <summary>
/// Adapts a single zerg <see cref="Connection"/> to Kestrel's <see cref="ConnectionContext"/>.
/// The <see cref="Transport"/> pipe is a <see cref="ConnectionDualPipe"/> wrapping the underlying
/// <see cref="ConnectionBase"/>, so Kestrel's HTTP parsers read/write through standard
/// <see cref="PipeReader"/> / <see cref="PipeWriter"/>.
/// </summary>
internal sealed class ZergConnectionContext : ConnectionContext,
    IConnectionIdFeature,
    IConnectionTransportFeature,
    IConnectionItemsFeature,
    IConnectionLifetimeFeature,
    IConnectionEndPointFeature
{
    private readonly Connection _connection;
    private readonly ConnectionDualPipe _pipe;
    private readonly CancellationTokenSource _connectionClosedCts = new();
    private readonly FeatureCollection _features = new();
    private bool _disposed;

    public ZergConnectionContext(Connection connection, EndPoint localEndPoint, long id)
    {
        _connection = connection;
        _pipe = new ConnectionDualPipe(connection);

        ConnectionId = $"zerg-{id:x}";
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = null; // zerg doesn't surface peer address today; can be added via getpeername
        Items = new ConnectionItems();
        ConnectionClosed = _connectionClosedCts.Token;

        _features.Set<IConnectionIdFeature>(this);
        _features.Set<IConnectionTransportFeature>(this);
        _features.Set<IConnectionItemsFeature>(this);
        _features.Set<IConnectionLifetimeFeature>(this);
        _features.Set<IConnectionEndPointFeature>(this);
    }

    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features => _features;
    public override IDictionary<object, object?> Items { get; set; }
    public override IDuplexPipe Transport
    {
        get => _pipe;
        set => throw new NotSupportedException("Transport is owned by the zerg transport adapter.");
    }
    public override CancellationToken ConnectionClosed { get; set; }
    public override EndPoint? LocalEndPoint { get; set; }
    public override EndPoint? RemoteEndPoint { get; set; }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        // Kestrel signals a forced close (protocol error, connection limit, etc.).
        // We can't actively close the fd from outside the reactor, so the best we can do is:
        //   1. cancel the lifetime token (so Kestrel's HTTP loop exits)
        //   2. complete the pipes with the abort exception so any pending read/flush wakes up.
        // The fd is closed by the reactor when the peer's FIN arrives or when the engine stops.
        try { _connectionClosedCts.Cancel(); } catch { /* ignore */ }
        _pipe.Input.Complete(abortReason);
        _pipe.Output.Complete(abortReason);
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        try { _connectionClosedCts.Cancel(); } catch { /* ignore */ }
        _pipe.Input.Complete();
        _pipe.Output.Complete();
        _connectionClosedCts.Dispose();
        return ValueTask.CompletedTask;
    }
}
