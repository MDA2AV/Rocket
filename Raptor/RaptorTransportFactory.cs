using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Raptor;

/// <summary>
/// Kestrel transport backed by io_uring. Each bound endpoint spawns a
/// <see cref="RaptorEngine"/> sized by <see cref="RaptorTransportOptions.ReactorCount"/>.
/// </summary>
public sealed class RaptorTransportFactory : IConnectionListenerFactory
{
    private readonly RaptorTransportOptions _options;
    private readonly ILogger<RaptorTransportFactory> _logger;

    public RaptorTransportFactory(IOptions<RaptorTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<RaptorTransportFactory>();
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not IPEndPoint ip)
            throw new NotSupportedException($"Raptor only supports {nameof(IPEndPoint)} (got {endpoint.GetType().Name}).");

        var engine = new RaptorEngine(
            (ushort)ip.Port, _options.ReactorCount, _options.RingEntries, _options.RecvBufferSize, _options.Backlog);
        engine.Start();

        _logger.LogInformation("[raptor] Bound :{Port} with {ReactorCount} reactor(s)", ip.Port, _options.ReactorCount);

        IConnectionListener listener = new RaptorConnectionListener(engine, ip);
        return ValueTask.FromResult(listener);
    }
}
