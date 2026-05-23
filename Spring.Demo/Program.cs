using Microsoft.Extensions.Logging;
using Spring;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);   // benchmark: silence per-request logs

builder.WebHost.UseKestrel(kestrel =>
{
    kestrel.ListenAnyIP(8080);
});

// SPRING=0 → Kestrel's default Socket transport (baseline). Otherwise the io_uring Spring transport.
if (Environment.GetEnvironmentVariable("SPRING") != "0")
{
    builder.WebHost.UseSpring(opts => opts.ReactorCount = Math.Max(1, 12));
}

var app = builder.Build();

app.MapGet("/", () => "Hello from Spring + Kestrel\n");

app.Run();
