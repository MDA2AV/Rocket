using Twinflow;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.WebHost
    .UseTwinflow(o => o.ReactorCount = 8)
    .ConfigureKestrel(o => o.ListenAnyIP(8080));

var app = builder.Build();
app.MapGet("/", () => "Hello World!");
app.Run();
