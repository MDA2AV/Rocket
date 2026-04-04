using zerg;
using Zerg.Core;
using zerg.Engine;
using zerg.Engine.Configs;

var engine = new Engine(new EngineOptions { Port = 8080, ReactorCount = 12 });
engine.Listen();

while (engine.ServerRunning)
{
    var connection = await engine.AcceptAsync(CancellationToken.None);
    if (connection is null) continue;
    _ = HandleAsync(connection);
}

static async Task HandleAsync(Connection connection)
{
    while (true)
    {
        var result = await connection.ReadAsync();
        if (result.IsClosed) break;

        var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
        // process rings.ToReadOnlySequence() ...
        rings.ReturnRingBuffers(connection.Reactor);

        connection.Write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"u8);
        await connection.FlushAsync();
        connection.ResetRead();
    }
}