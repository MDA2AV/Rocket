using System.Text.Json;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(8080));   // default Kestrel socket transport

var app = builder.Build();

// Same knob + same object as Minima's handler: serialize a WORK_ITEMS-element object to
// JSON per request and discard it (the work stands in for a serializing endpoint).
// 0 / unset = no work (plain "ok"). The handler already runs on the thread pool here —
// no Task.Run needed — which is exactly Kestrel's model.
int workItems = int.TryParse(Environment.GetEnvironmentVariable("WORK_ITEMS"), out int n) ? n : 0;
Payload largeObject = BuildPayload(Math.Max(workItems, 1));

app.MapGet("/", () =>
{
    if (workItems > 0)
    {
        _ = JsonSerializer.SerializeToUtf8Bytes(largeObject);
    }
    return "ok";
});

app.Run();

static Payload BuildPayload(int count)
{
    var items = new Item[count];
    for (int i = 0; i < count; i++)
    {
        items[i] = new Item(i, $"item-{i}", i * 1.5, (i & 1) == 0, $"category-{i % 8}");
    }
    return new Payload(DateTime.UtcNow.ToString("O"), count, items);
}

internal sealed record Item(int Id, string Name, double Value, bool Active, string Category);
internal sealed record Payload(string Generated, int Count, Item[] Items);
