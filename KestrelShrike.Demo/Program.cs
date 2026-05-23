using Microsoft.Extensions.Logging;
using KestrelShrike;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);   // benchmark: silence per-request logs

builder.WebHost.UseKestrel(kestrel =>
{
    kestrel.ListenAnyIP(8080);
});

// SHRIKE=0 → Kestrel's default Socket transport (baseline). Otherwise the epoll Shrike transport.
if (Environment.GetEnvironmentVariable("SHRIKE") != "0")
{
    builder.WebHost.UseShrike(opts => opts.ReactorCount = Math.Max(1, 4));
}

var app = builder.Build();

app.MapGet("/", () => "Hello from Shrike + Kestrel\n");

app.Run();
