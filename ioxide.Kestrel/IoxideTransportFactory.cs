using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ioxide.Kestrel;

/// <summary>
/// Kestrel transport that runs each connection on an ioxide reactor fleet. Kestrel calls
/// <see cref="BindAsync"/> once per configured endpoint; each call spins up its own set of reactors.
/// </summary>
internal sealed class IoxideTransportFactory : IConnectionListenerFactory
{
    private readonly IoxideTransportOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public IoxideTransportFactory(IOptions<IoxideTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not IPEndPoint ipEndpoint)
        {
            throw new NotSupportedException($"ioxide transport only supports {nameof(IPEndPoint)} (got {endpoint.GetType().Name}).");
        }

        var logger = _loggerFactory.CreateLogger<IoxideConnectionListener>();
        IConnectionListener listener = new IoxideConnectionListener(ipEndpoint, _options, logger);
        return ValueTask.FromResult(listener);
    }
}
