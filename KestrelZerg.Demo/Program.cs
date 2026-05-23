using KestrelZerg;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(8080);
    })
    .UseZerg(opts =>
    {
        opts.ReactorCount = Math.Max(1, 32);
    });

var app = builder.Build();

app.MapGet("/", () => "Hello from zerg + Kestrel\n");

app.MapGet("/info", (HttpContext ctx) => new ConnectionInfo(
    ConnectionId: ctx.Connection.Id,
    LocalIp: ctx.Connection.LocalIpAddress?.ToString(),
    LocalPort: ctx.Connection.LocalPort,
    RemoteIp: ctx.Connection.RemoteIpAddress?.ToString(),
    RemotePort: ctx.Connection.RemotePort,
    Method: ctx.Request.Method,
    Path: ctx.Request.Path.Value));

app.MapPost("/echo", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Text(body);
});

app.Run();

internal sealed record ConnectionInfo(
    string ConnectionId,
    string? LocalIp,
    int LocalPort,
    string? RemoteIp,
    int RemotePort,
    string Method,
    string? Path);
