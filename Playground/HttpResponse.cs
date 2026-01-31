using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using URocket.Connection;
using URocket.Utils.UnmanagedMemoryManager;
using ReadResult = URocket.Utils.ReadResult;

namespace Playground;

public class HttpResponse
{
    internal static async Task HandleConnectionStreamAsync(Connection connection)
    {
        var stream = new ConnectionStream(connection);
        var reader = PipeReader.Create(stream);
        var writer = PipeWriter.Create(stream);
        
        while (true)
        {
            var result = await reader.ReadAsync();
            connection.ResetRead();
            
            var buffer = result.Buffer;
            var isCompleted = result.IsCompleted;
            if (buffer.IsEmpty && isCompleted)
                return;
            
            var sequenceReader = new SequenceReader<byte>(buffer);

            if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> msg, "\r\n\r\n"u8, true))
            {
                var data = Encoding.UTF8.GetString(buffer.ToArray());
                Console.WriteLine(data);
                continue;
            }
            reader.AdvanceTo(buffer.End);
            
            writer.Write("HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8);
            await writer.FlushAsync();
        }
    }
    
    internal static async Task HandleConnectionAsync(Connection connection)
    {
        while (true)
        {
            ReadResult result = await connection.ReadAsync();
            if (result.IsClosed)
                break;

            // Get all ring buffers data
            UnmanagedMemoryManager[] rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
            ReadOnlySequence<byte> sequence = rings.ToReadOnlySequence();

            // Process received data...

            // Return rings to the kernel
            foreach (UnmanagedMemoryManager ring in rings)
                connection.ReturnRing(ring.BufferId);

            // Write the response directly into the connection slab
            ReadOnlySpan<byte> msg =
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;

            connection.InnerWrite(msg);

            // New: async flush barrier (wait until fully flushed to kernel)
            await connection.InnerFlushAsync();

            // Ready for next read cycle
            connection.ResetRead();
        }

        Console.WriteLine("HandleConnectionAsync exited.");
    }
}