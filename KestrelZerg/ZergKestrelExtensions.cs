using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KestrelZerg;

public static class ZergKestrelExtensions
{
    /// <summary>
    /// Replaces Kestrel's default Sockets transport with the zerg transport.
    /// Must be called AFTER <c>UseKestrel</c> (or rely on the Kestrel registration that
    /// <see cref="WebApplication.CreateBuilder"/> performs implicitly) — this evicts any
    /// previously-registered <see cref="IConnectionListenerFactory"/> and installs zerg.
    /// </summary>
    public static IWebHostBuilder UseZerg(this IWebHostBuilder builder, Action<ZergTransportOptions>? configure = null)
    {
        return builder.ConfigureServices(services =>
        {
            services.AddOptions<ZergTransportOptions>();
            if (configure != null)
                services.Configure(configure);

            services.RemoveAll<IConnectionListenerFactory>();
            services.AddSingleton<IConnectionListenerFactory, ZergTransportFactory>();
        });
    }
}
