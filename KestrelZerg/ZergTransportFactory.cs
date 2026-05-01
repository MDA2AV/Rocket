using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using zerg.Engine;
using zerg.Engine.Configs;

namespace KestrelZerg;

/// <summary>
/// Kestrel transport that runs each connection on a zerg reactor. Bind one IPEndPoint per call
/// (Kestrel calls <see cref="BindAsync"/> once per configured endpoint); each call spawns its own
/// <see cref="Engine"/> sized by <see cref="ZergTransportOptions.ReactorCount"/>.
/// </summary>
public sealed class ZergTransportFactory : IConnectionListenerFactory
{
    private readonly ZergTransportOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public ZergTransportFactory(IOptions<ZergTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not IPEndPoint ipEndpoint)
            throw new NotSupportedException($"zerg transport only supports {nameof(IPEndPoint)} (got {endpoint.GetType().Name}).");

        var engineOptions = new EngineOptions
        {
            Ip = ipEndpoint.Address.ToString(),
            Port = (ushort)ipEndpoint.Port,
            ReactorCount = _options.ReactorCount,
            Backlog = _options.Backlog,
            AcceptorConfig = new AcceptorConfig(IPVersion: _options.IPVersion),
            ReactorConfigs = _options.ReactorConfigs!
        };

        var engine = new Engine(engineOptions);
        engine.Listen();

        var logger = _loggerFactory.CreateLogger<ZergConnectionListener>();
        logger.LogInformation("[zerg] Bound {Endpoint} with {ReactorCount} reactor(s)", ipEndpoint, _options.ReactorCount);
        IConnectionListener listener = new ZergConnectionListener(engine, ipEndpoint, logger);
        return ValueTask.FromResult(listener);
    }
}
