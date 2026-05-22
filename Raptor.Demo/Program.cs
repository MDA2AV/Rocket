using Microsoft.Extensions.Logging;
using Raptor;

var builder = WebApplication.CreateBuilder(args);

// Silence per-request Information logging — it spams the console and, more
// importantly, a synchronized console write per request badly skews throughput.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.WebHost.UseKestrel(kestrel =>
{
    kestrel.ListenAnyIP(8080);
});

// RAPTOR=0 → Kestrel's default Socket transport (baseline). Otherwise io_uring Raptor.
if (Environment.GetEnvironmentVariable("RAPTOR") != "0")
{
    builder.WebHost.UseRaptor(opts =>
    {
        opts.ReactorCount = Math.Max(1, 12);
    });
}

var app = builder.Build();

app.MapGet("/", () => "Hello from Raptor\n");

app.Run();
