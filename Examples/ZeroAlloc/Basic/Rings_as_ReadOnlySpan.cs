using zerg;
using zerg.Utils.UnmanagedMemoryManager;

namespace Examples.ZeroAlloc.Basic;

internal sealed class Rings_as_ReadOnlySpan
{
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