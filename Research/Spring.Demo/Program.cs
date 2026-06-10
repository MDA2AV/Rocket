using Microsoft.Extensions.Logging;
using Spring;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);   // benchmark: silence per-request logs

builder.WebHost.UseKestrel(kestrel =>
{
    kestrel.ListenAnyIP(8080);
});

builder.WebHost.UseSpring(opts => opts.ReactorCount = Math.Max(1, 12));

var app = builder.Build();

app.MapGet("/", () => "Hello, World!\n");

app.Run();
