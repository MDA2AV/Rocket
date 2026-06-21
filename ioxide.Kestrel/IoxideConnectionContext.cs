using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using ioxide;

namespace ioxide.Kestrel;

/// <summary>
/// Adapts a single ioxide <see cref="Connection"/> to Kestrel's <see cref="ConnectionContext"/>. The
/// transport is a <see cref="HopDuplexPipe"/> (BCL pipes whose reader schedulers route to the reactor),
/// so Kestrel's read → parse → handle → send loop runs pinned to the reactor thread.
/// </summary>
internal sealed class IoxideConnectionContext : ConnectionContext,
    IConnectionIdFeature,
    IConnectionTransportFeature,
    IConnectionItemsFeature,
    IConnectionLifetimeFeature,
    IConnectionEndPointFeature
{
    private readonly HopDuplexPipe _pipe;
    private readonly CancellationTokenSource _connectionClosedCts = new();
    private readonly FeatureCollection _features = new();

    // Completed when Kestrel is done with the connection (DisposeAsync, or Abort). The ioxide Handle
    // callback awaits this and only then DecRefs the connection.
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposed;

    public IoxideConnectionContext(Connection connection, Reactor reactor, EndPoint localEndPoint, long id)
    {
        _pipe = new HopDuplexPipe(connection, reactor);

        ConnectionId = $"ioxide-{id:x}";
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = null;
        Items = new ConnectionItems();
        ConnectionClosed = _connectionClosedCts.Token;

        _features.Set<IConnectionIdFeature>(this);
        _features.Set<IConnectionTransportFeature>(this);
        _features.Set<IConnectionItemsFeature>(this);
        _features.Set<IConnectionLifetimeFeature>(this);
        _features.Set<IConnectionEndPointFeature>(this);
    }

    /// <summary>Resolves once Kestrel has finished with this connection; the reactor's Handle callback awaits it.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Launches the transport pumps. Must be called on the reactor thread (from the Handle callback).</summary>
    public void StartPumps() => _pipe.Start();

    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features => _features;
    public override IDictionary<object, object?> Items { get; set; }

    public override IDuplexPipe Transport
    {
        get => _pipe;
        set => throw new NotSupportedException("Transport is owned by the ioxide transport adapter.");
    }

    public override CancellationToken ConnectionClosed { get; set; }
    public override EndPoint? LocalEndPoint { get; set; }
    public override EndPoint? RemoteEndPoint { get; set; }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        // Forced close: signal the lifetime token. The pumps are unwound (reactor-safely) in DisposeAsync,
        // which Kestrel calls during connection cleanup after Abort.
        try { _connectionClosedCts.Cancel(); } catch { /* ignore */ }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try { _connectionClosedCts.Cancel(); } catch { /* ignore */ }

        // Unwind the recv/send pumps (returns held recv buffers, stops the send loop).
        await _pipe.DisposeAsync().ConfigureAwait(false);

        _connectionClosedCts.Dispose();

        // Release the reactor's Handle callback, which DecRefs the connection (-> recycle).
        _completion.TrySetResult();
    }
}
