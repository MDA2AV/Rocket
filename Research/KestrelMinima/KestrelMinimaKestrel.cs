using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KestrelMinima;

internal sealed class KestrelMinimaConnectionContext : ConnectionContext,
    IConnectionIdFeature, IConnectionTransportFeature, IConnectionItemsFeature,
    IConnectionLifetimeFeature, IConnectionEndPointFeature
{
    private static long s_id;

    private readonly Connection _conn;
    private readonly ConnectionDualPipe _pipe;
    private readonly CancellationTokenSource _closedCts = new();
    private readonly FeatureCollection _features = new();
    private bool _disposed;

    public KestrelMinimaConnectionContext(Connection conn, EndPoint? localEndPoint)
    {
        _conn = conn;
        _pipe = new ConnectionDualPipe(conn);

        ConnectionId = $"kestrel-minima-{Interlocked.Increment(ref s_id):x}";
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
        get => _pipe;
        set => throw new NotSupportedException("Transport is owned by the KestrelMinima transport.");
    }
    public override CancellationToken ConnectionClosed { get; set; }
    public override EndPoint? LocalEndPoint { get; set; }
    public override EndPoint? RemoteEndPoint { get; set; }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        try { _closedCts.Cancel(); } catch { }
        try { _pipe.Input.Complete(abortReason); } catch { }
        try { _pipe.Output.Complete(abortReason); } catch { }
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _closedCts.Cancel(); } catch { }
        try { _pipe.Input.Complete(); } catch { }
        try { _pipe.Output.Complete(); } catch { }
        _closedCts.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class KestrelMinimaConnectionListener : IConnectionListener
{
    private readonly KestrelMinimaEngine _engine;

    public KestrelMinimaConnectionListener(KestrelMinimaEngine engine, EndPoint endpoint)
    {
        _engine = engine;
        EndPoint = endpoint;
    }

    public EndPoint EndPoint { get; }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Connection conn = await _engine.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new KestrelMinimaConnectionContext(conn, EndPoint);
        }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException)     { return null; }
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default) { _engine.Stop(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { _engine.Stop(); return ValueTask.CompletedTask; }
}

public sealed class KestrelMinimaTransportOptions
{
    public int ReactorCount { get; set; } = Math.Max(1, Environment.ProcessorCount);
}

public sealed class KestrelMinimaTransportFactory : IConnectionListenerFactory
{
    private readonly KestrelMinimaTransportOptions _options;
    private readonly ILogger<KestrelMinimaTransportFactory> _logger;

    public KestrelMinimaTransportFactory(IOptions<KestrelMinimaTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<KestrelMinimaTransportFactory>();
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not IPEndPoint ip)
        {
            throw new NotSupportedException(
                $"KestrelMinima only supports {nameof(IPEndPoint)} (got {endpoint.GetType().Name}).");
        }

        var config = new ServerConfig { Port = (ushort)ip.Port, ReactorCount = _options.ReactorCount, Incremental = false };
        var engine = new KestrelMinimaEngine(config);
        engine.Start();
        _logger.LogInformation("[kestrel-minima] Bound :{Port} with {ReactorCount} io_uring reactor(s) (fire-and-forget send)", ip.Port, _options.ReactorCount);

        IConnectionListener listener = new KestrelMinimaConnectionListener(engine, ip);
        return ValueTask.FromResult(listener);
    }
}

public static class KestrelMinimaKestrelExtensions
{
    /// <summary>
    /// Replace Kestrel's socket transport with KestrelMinima: a per-core io_uring reactor for
    /// accept/recv and a fire-and-forget io_uring send (FlushAsync enqueues an SQE and returns
    /// synchronously — no IValueTaskSource awaiter scheduling). Linux only, HTTP/1.1 plaintext.
    /// </summary>
    public static IWebHostBuilder UseKestrelMinima(this IWebHostBuilder builder, Action<KestrelMinimaTransportOptions>? configure = null)
    {
        builder.ConfigureServices(services =>
        {
            if (configure is not null) services.Configure(configure);
            services.AddSingleton<IConnectionListenerFactory, KestrelMinimaTransportFactory>();
        });
        return builder;
    }
}
