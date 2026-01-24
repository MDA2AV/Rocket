using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using URocket.Connection;
using URocket.Utils;
using URocket.Utils.ReadOnlySequence;
using URocket.Utils.UnmanagedMemoryManager;

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

            // Building an UnmanagedMemoryManager wrapping the msg, this step has no data allocation
            // however msg must be fixed/pinned because the engine reactor's needs to pass a byte* to liburing
            unsafe
            {
                var unmanagedMemory = new UnmanagedMemoryManager(
                    (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(msg)),
                    msg.Length,
                    false); // Setting freeable to false signaling that this unmanaged memory should not be freed because it comes from an u8 literal
                
                if (!connection.Write(new WriteItem(unmanagedMemory, connection.ClientFd)))
                    throw new InvalidOperationException("Failed to write response");
            }
            
            // Signal that written data can be flushed
            connection.Flush();
            // Signal we are ready for a new read
            connection.ResetRead();
        }
    }
}