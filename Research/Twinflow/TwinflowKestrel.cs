using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Twinflow;

internal sealed class TwinflowDuplexPipe : IDuplexPipe
{
    public TwinflowDuplexPipe(TwinflowConnection conn)
    {
        Input = conn.Input.Reader; 
        Output = conn.Output.Writer;
    }
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
}

internal sealed class TwinflowConnectionContext : ConnectionContext,
    IConnectionIdFeature, IConnectionTransportFeature, IConnectionItemsFeature,
    IConnectionLifetimeFeature, IConnectionEndPointFeature
{
    private readonly TwinflowConnection _conn;
    private readonly TwinflowDuplexPipe _pipe;
    private readonly CancellationTokenSource _closedCts = new();
    private readonly FeatureCollection _features = new();
    private bool _disposed;

    public TwinflowConnectionContext(TwinflowConnection conn, EndPoint? localEndPoint)
    {
        _conn = conn;
        _pipe = new TwinflowDuplexPipe(conn);

        ConnectionId = $"twinflow-{conn.Id:x}";
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
        set => throw new NotSupportedException("Transport is owned by the Twinflow transport.");
    }
    public override CancellationToken ConnectionClosed { get; set; }
    public override EndPoint? LocalEndPoint { get; set; }
    public override EndPoint? RemoteEndPoint { get; set; }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        try
        {
            _closedCts.Cancel();
        }
        catch
        {
            
        }
        _conn.OnKestrelClose();
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        
        _disposed = true;
        try
        {
            _closedCts.Cancel();
        }
        catch
        {
            
        }
        _conn.OnKestrelClose();
        _closedCts.Dispose();
        
        return ValueTask.CompletedTask;
    }
}

internal sealed class TwinflowConnectionListener : IConnectionListener
{
    private readonly TwinflowEngine _engine;
    public TwinflowConnectionListener(TwinflowEngine engine, EndPoint endpoint) { _engine = engine; EndPoint = endpoint; }
    public EndPoint EndPoint { get; }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            TwinflowConnection conn = await _engine.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new TwinflowConnectionContext(conn, EndPoint);
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

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default) { _engine.Stop(); return ValueTask.CompletedTask; }

    public ValueTask DisposeAsync()
    {
        _engine.Stop(); 
        
        return ValueTask.CompletedTask;
    }
}

public sealed class TwinflowTransportOptions
{
    public int ReactorCount { get; set; } = Math.Max(1, Environment.ProcessorCount);
}

public sealed class TwinflowTransportFactory : IConnectionListenerFactory
{
    private readonly TwinflowTransportOptions _options;
    private readonly ILogger<TwinflowTransportFactory> _logger;

    public TwinflowTransportFactory(IOptions<TwinflowTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<TwinflowTransportFactory>();
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not IPEndPoint ip)
        {
            throw new NotSupportedException(
                $"Twinflow only supports {nameof(IPEndPoint)} (got {endpoint.GetType().Name}).");
        }

        var engine = new TwinflowEngine((ushort)ip.Port, _options.ReactorCount);
        engine.Start();
        _logger.LogInformation("[twinflow] Bound :{Port} with {ReactorCount} io_uring reactor(s)", ip.Port, _options.ReactorCount);

        IConnectionListener listener = new TwinflowConnectionListener(engine, ip);
        
        return ValueTask.FromResult(listener);
    }
}

public static class TwinflowKestrelExtensions
{
    /// <summary>
    /// Replace Kestrel's socket transport with Twinflow: a per-core io_uring reactor for
    /// accept/recv and a thread-safe libc send() for the response. Linux only.
    /// </summary>
    public static IWebHostBuilder UseTwinflow(this IWebHostBuilder builder, Action<TwinflowTransportOptions>? configure = null)
    {
        builder.ConfigureServices(services =>
        {
            if (configure is not null)
            {
                services.Configure(configure);
            }
            services.AddSingleton<IConnectionListenerFactory, TwinflowTransportFactory>();
        });
        return builder;
    }
}
