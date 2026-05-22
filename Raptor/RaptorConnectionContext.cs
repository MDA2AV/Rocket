using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Raptor;

/// <summary>
/// Adapts a Raptor connection to Kestrel. Transport.Input is the connection's
/// input Pipe reader (reactor-fed); Transport.Output is its output Pipe writer
/// (drained by the connection's send pump). Both are plain BCL Pipes.
/// </summary>
internal sealed class RaptorConnectionContext : ConnectionContext,
    IConnectionIdFeature,
    IConnectionTransportFeature,
    IConnectionItemsFeature,
    IConnectionLifetimeFeature,
    IConnectionEndPointFeature
{
    private readonly RaptorConnection _conn;
    private readonly IDuplexPipe _transport;
    private readonly CancellationTokenSource _closedCts = new();
    private readonly FeatureCollection _features = new();
    private bool _disposed;

    public RaptorConnectionContext(RaptorConnection conn, EndPoint? localEndPoint)
    {
        _conn = conn;
        _transport = new DuplexPipe(conn.Input.Reader, conn.Output.Writer);

        ConnectionId = $"raptor-{conn.Id:x}";
        LocalEndPoint = localEndPoint;
        Items = new ConnectionItems();
        ConnectionClosed = _closedCts.Token;

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
        get => _transport;
        set => throw new NotSupportedException("Transport is owned by the Raptor transport.");
    }
    public override CancellationToken ConnectionClosed { get; set; }
    public override EndPoint? LocalEndPoint { get; set; }
    public override EndPoint? RemoteEndPoint { get; set; }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        try { _closedCts.Cancel(); } catch { }
        _conn.Output.Reader.CancelPendingRead();              // wake the send pump
        try { _conn.Input.Writer.Complete(abortReason); } catch { }
        try { _conn.Output.Writer.Complete(abortReason); } catch { }
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _closedCts.Cancel(); } catch { }
        try { _conn.Input.Reader.Complete(); } catch { }
        try { _conn.Output.Writer.Complete(); } catch { }
        _closedCts.Dispose();
        return ValueTask.CompletedTask;
    }
}
