using ioxide.Kestrel;
using Microsoft.Extensions.Logging;

// A minimal ASP.NET Core app that runs on either the ioxide io_uring transport or Kestrel's stock
// sockets transport. Pick with the TRANSPORT environment variable (default: ioxide):
//
//   TRANSPORT=ioxide  dotnet run     # ioxide.Kestrel transport (io_uring, one reactor per core)
//   TRANSPORT=sockets dotnet run     # stock Kestrel sockets transport (the framework default)
//
// Then: curl http://localhost:8080/  and  curl http://localhost:8080/plaintext

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();   // turn off all logging (no per-request info: lines)

var transport = (Environment.GetEnvironmentVariable("TRANSPORT") ?? "ioxide").Trim().ToLowerInvariant();

builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(8080));

switch (transport)
{
    case "ioxide":
        builder.WebHost.UseIoxide(o => o.ReactorCount = 16);   // io_uring transport, 16 reactors (one ring per thread)
        break;

    case "sockets":
    case "kestrel":
        // Stock Kestrel sockets transport — the framework default, nothing to wire up.
        break;

    default:
        Console.Error.WriteLine($"Unknown TRANSPORT '{transport}'. Use 'ioxide' or 'sockets'.");
        return;
}

var app = builder.Build();

app.MapGet("/", () => $"Hello from ioxide.Kestrel! transport={transport}");
app.MapGet("/plaintext", () => "Hello, World!");

Console.WriteLine($"[Examples.AspNet] listening on http://localhost:8080  (transport={transport})");
app.Run();
