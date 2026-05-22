using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KestrelShrike;

internal sealed class DuplexPipe : IDuplexPipe
{
    public DuplexPipe(PipeReader input, PipeWriter output) { Input = input; Output = output; }
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
}

internal sealed class ShrikeConnectionContext : ConnectionContext,
    IConnectionIdFeature, IConnectionTransportFeature, IConnectionItemsFeature,
    IConnectionLifetimeFeature, IConnectionEndPointFeature
{
    private static long s_id;

    private readonly EpollConnection _conn;
    private readonly IDuplexPipe _transport;
    private readonly CancellationTokenSource _closedCts = new();
    private readonly FeatureCollection _features = new();
    private bool _disposed;

    public ShrikeConnectionContext(EpollConnection conn, EndPoint? localEndPoint)
    {
        _conn = conn;
        _transport = new DuplexPipe(conn.Input.Reader, conn.Output.Writer);

        ConnectionId = $"shrike-{Interlocked.Increment(ref s_id):x}";
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
        set => throw new NotSupportedException("Transport is owned by the Shrike transport.");
    }
    public override CancellationToken ConnectionClosed { get; set; }
    public override EndPoint? LocalEndPoint { get; set; }
    public override EndPoint? RemoteEndPoint { get; set; }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        try { _closedCts.Cancel(); } catch { }
        try { _conn.Output.Reader.CancelPendingRead(); } catch { }   // wake the pump
        try { _conn.Output.Writer.Complete(abortReason); } catch { } // pump exits
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _closedCts.Cancel(); } catch { }
        try { _conn.Input.Reader.Complete(); } catch { }    // Kestrel done reading
        try { _conn.Output.Writer.Complete(); } catch { }   // pump exits
        _closedCts.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ShrikeConnectionListener : IConnectionListener
{
    private readonly EpollEngine _engine;

    public ShrikeConnectionListener(EpollEngine engine, EndPoint endpoint)
    {
        _engine = engine;
        EndPoint = endpoint;
    }

    public EndPoint EndPoint { get; }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EpollConnection conn = await _engine.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new ShrikeConnectionContext(conn, EndPoint);
        }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException)     { return null; }
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default) { _engine.Stop(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { _engine.Stop(); return ValueTask.CompletedTask; }
}

public sealed class ShrikeTransportOptions
{
    public int ReactorCount { get; set; } = Math.Max(1, Environment.ProcessorCount);
    public int Backlog { get; set; } = 16384;
    public int MaxEventsPerWake { get; set; } = 512;
}

public sealed class ShrikeTransportFactory : IConnectionListenerFactory
{
    private readonly ShrikeTransportOptions _options;
    private readonly ILogger<ShrikeTransportFactory> _logger;

    public ShrikeTransportFactory(IOptions<ShrikeTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<ShrikeTransportFactory>();
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not IPEndPoint ip)
            throw new NotSupportedException($"Shrike only supports {nameof(IPEndPoint)} (got {endpoint.GetType().Name}).");

        var engine = new EpollEngine((ushort)ip.Port, _options.ReactorCount, _options.Backlog, _options.MaxEventsPerWake);
        engine.Start();
        _logger.LogInformation("[shrike-k] Bound :{Port} with {ReactorCount} epoll reactor(s) (SO_REUSEPORT)", ip.Port, _options.ReactorCount);

        IConnectionListener listener = new ShrikeConnectionListener(engine, ip);
        return ValueTask.FromResult(listener);
    }
}

public static class ShrikeKestrelExtensions
{
    /// <summary>Replace Kestrel's socket transport with the epoll-based Shrike transport.</summary>
    public static IWebHostBuilder UseShrike(this IWebHostBuilder builder, Action<ShrikeTransportOptions>? configure = null)
    {
        builder.ConfigureServices(services =>
        {
            if (configure is not null) services.Configure(configure);
            services.AddSingleton<IConnectionListenerFactory, ShrikeTransportFactory>();
        });
        return builder;
    }
}
