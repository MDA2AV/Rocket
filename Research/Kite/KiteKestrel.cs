using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kite;

internal sealed class KiteDuplexPipe : IDuplexPipe
{
    public KiteDuplexPipe(KiteConnection conn)
    {
        Input = conn.Input.Reader; 
        Output = conn.Output.Writer;
    }
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
}

internal sealed class KiteConnectionContext : ConnectionContext,
    IConnectionIdFeature, IConnectionTransportFeature, IConnectionItemsFeature,
    IConnectionLifetimeFeature, IConnectionEndPointFeature
{
    private readonly KiteConnection _conn;
    private readonly KiteDuplexPipe _pipe;
    private readonly CancellationTokenSource _closedCts = new();
    private readonly FeatureCollection _features = new();
    private bool _disposed;

    public KiteConnectionContext(KiteConnection conn, EndPoint? localEndPoint)
    {
        _conn = conn;
        _pipe = new KiteDuplexPipe(conn);

        ConnectionId = $"kite-{conn.Id:x}";
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
        set => throw new NotSupportedException("Transport is owned by the Kite transport.");
    }
    public override CancellationToken ConnectionClosed { get; set; }
    public override EndPoint? LocalEndPoint { get; set; }
    public override EndPoint? RemoteEndPoint { get; set; }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        try { _closedCts.Cancel(); } catch { }
        _conn.OnKestrelClose();
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _closedCts.Cancel(); } catch { }
        _conn.OnKestrelClose();
        _closedCts.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class KiteConnectionListener : IConnectionListener
{
    private readonly KiteEngine _engine;
    public KiteConnectionListener(KiteEngine engine, EndPoint endpoint) { _engine = engine; EndPoint = endpoint; }
    public EndPoint EndPoint { get; }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            KiteConnection conn = await _engine.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new KiteConnectionContext(conn, EndPoint);
        }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException)     { return null; }
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default) { _engine.Stop(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { _engine.Stop(); return ValueTask.CompletedTask; }
}

public sealed class KiteTransportOptions
{
    public int ReactorCount { get; set; } = Math.Max(1, Environment.ProcessorCount);
}

public sealed class KiteTransportFactory : IConnectionListenerFactory
{
    private readonly KiteTransportOptions _options;
    private readonly ILogger<KiteTransportFactory> _logger;

    public KiteTransportFactory(IOptions<KiteTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<KiteTransportFactory>();
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not IPEndPoint ip)
            throw new NotSupportedException($"Kite only supports {nameof(IPEndPoint)} (got {endpoint.GetType().Name}).");

        var engine = new KiteEngine((ushort)ip.Port, _options.ReactorCount);
        engine.Start();
        _logger.LogInformation("[kite] Bound :{Port} with {ReactorCount} io_uring reactor(s) (lean, libc send)", ip.Port, _options.ReactorCount);

        IConnectionListener listener = new KiteConnectionListener(engine, ip);
        return ValueTask.FromResult(listener);
    }
}

public static class KiteKestrelExtensions
{
    /// <summary>Lean io_uring recv loop + libc send pump — KestrelShrike's design with io_uring instead of epoll.</summary>
    public static IWebHostBuilder UseKite(this IWebHostBuilder builder, Action<KiteTransportOptions>? configure = null)
    {
        builder.ConfigureServices(services =>
        {
            if (configure is not null) services.Configure(configure);
            services.AddSingleton<IConnectionListenerFactory, KiteTransportFactory>();
        });
        return builder;
    }
}
