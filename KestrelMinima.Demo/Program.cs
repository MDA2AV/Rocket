using KestrelMinima;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.WebHost
    .UseKestrelMinima(o => o.ReactorCount = 8)
    .ConfigureKestrel(o => o.ListenAnyIP(8080));

var app = builder.Build();
app.MapGet("/", () => "Hello World!");
app.Run();
