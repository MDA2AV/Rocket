using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Raptor;

public static class RaptorKestrelExtensions
{
    /// <summary>Replace Kestrel's socket transport with the io_uring-based Raptor transport.</summary>
    public static IWebHostBuilder UseRaptor(this IWebHostBuilder builder, Action<RaptorTransportOptions>? configure = null)
    {
        builder.ConfigureServices(services =>
        {
            if (configure is not null) services.Configure(configure);
            services.AddSingleton<IConnectionListenerFactory, RaptorTransportFactory>();
        });
        return builder;
    }
}
