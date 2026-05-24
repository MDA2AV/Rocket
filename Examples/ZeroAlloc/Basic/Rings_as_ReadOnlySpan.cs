using System.Text.Json;
using zerg;
using zerg.core;

namespace Examples.ZeroAlloc.Basic;

internal sealed class Rings_as_ReadOnlySpan
{
    internal sealed record Item(int Id, string Name, double Value, bool Active, string Category);
    internal sealed record Payload(string Generated, int Count, Item[] Items);
    
    // Real async-work knob: serialize an in-memory object of WORK_ITEMS elements to JSON
    // on the THREAD POOL (via Task.Run) per request. 0 / unset = disabled (pure inline
    // reactor path). Genuine CPU + allocation, not a busy-spin.
    private static readonly int WorkItems = 50;

    private static readonly Payload LargeObject = BuildPayload(Math.Max(WorkItems, 1));

    private static Payload BuildPayload(int count)
    {
        var items = new Item[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new Item(i, $"item-{i}", i * 1.5, (i & 1) == 0, $"category-{i % 8}");
        }
        return new Payload(DateTime.UtcNow.ToString("O"), count, items);
    }
    
    internal static async Task HandleConnectionAsync(Connection connection)
    {
        while (true)
        {
            var result = await connection.ReadAsync();
            if (result.IsClosed)
                break;
            
            // Get all ring buffers data
            var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
            // Create a ReadOnlySequence<byte> to easily slice the data
            var sequence = rings.ToReadOnlySequence();
            
            // Process received data...
            if (WorkItems > 0)
            {
                //_ = await Task.Run(static () => JsonSerializer.SerializeToUtf8Bytes(LargeObject));
                JsonSerializer.SerializeToUtf8Bytes(LargeObject);
            }
            
            // Return rings to the kernel
            foreach (var ring in rings)
                connection.ReturnRing(ring.BufferId);
            
            // Write the response
            var msg =
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;

            connection.Write(msg);
            
            // Signal that written data can be flushed
            await connection.FlushAsync();
            // Signal we are ready for a new read
            connection.ResetRead();
        }
    }
}