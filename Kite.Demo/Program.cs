using Kite;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseKite(o => o.ReactorCount = 8);
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(8080));

var app = builder.Build();
app.MapGet("/", () => "Hello from Kite + Kestrel");
app.Run();
