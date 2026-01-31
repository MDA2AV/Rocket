using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using URocket.Connection;
using URocket.Utils;
using URocket.Utils.UnmanagedMemoryManager;

namespace Examples.ZeroAlloc.Advanced;

internal sealed class ZeroAlloc_Advanced_SingleRing_ConnectionHandler
{
    private readonly unsafe byte* _inflightData;
    private int _inflightTail;
    private readonly int _length;

    public unsafe ZeroAlloc_Advanced_SingleRing_ConnectionHandler(int length = 1024 * 16)
    {
        _length = length;
        
        // Allocating an unmanaged byte slab to store inflight data
        _inflightData = (byte*)NativeMemory.AlignedAlloc((nuint)_length, 64);

        _inflightTail = 0;
    }

    // Zero allocation read and write example
    // Single ring read per loop cycle
    // No Peeking
    internal async Task HandleConnectionAsync(Connection connection)
    {
        try
        {
            while (true) // Outer loop, iterates everytime we read more data from the wire
            {
                var result = await connection.ReadAsync(); // Read data from the wire
                if (result.IsClosed)
                    break;

                if (HandleInnerConnection(connection, ref result))
                {
                    await connection.InnerFlushAsync(); // Mark data to be ready to be flushed
                }

                // Reset connection's ManualResetValueTaskSourceCore<ReadResult>
                connection.ResetRead();
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private unsafe bool HandleInnerConnection(Connection connection, ref ReadResult result)
    {
        // Signals where at least one request was handled and we can flush written response data
        var flushable = false;

        // Get the "head" ring - first ring received
        if (!connection.TryGetRing(result.TailSnapshot, out var ring)) 
            return flushable;
        
        while (true) // Inner loop, iterates every request handling, it can happen 0, 1 or n times
            // per read as we may read an incomplete, one single or multiple requests at once
        {
            bool found;
            int position;
            // Here we want to merge existing inflight data with the just read new data
            if (_inflightTail == 0)
            {
                // Hot path, typically each ring contains a full request and inflight buffer isn't used
                var span = new ReadOnlySpan<byte>(ring.Ptr, ring.Length);
                found = HandleNoInflight(connection, ref span, out position);
            }
            else
            {
                // Cold path
                found = HandleWithInflight(connection, 
                    [ring.AsUnmanagedMemoryManager(), new(_inflightData, _inflightTail)],
                    out position);

                if (found)  // a request was handled so inflight data can be discarded
                    _inflightTail = 0;
            }

            if (!found)
            {
                // \r\n\r\n not found, full headers are not yet available
                // Copy the ring data to the inflight buffer and read more
                Buffer.MemoryCopy(
                    ring.Ptr, // source
                    _inflightData + _inflightTail, // destination
                    _length - _inflightTail, // destinationSizeInBytes
                    ring.Length); // sourceBytesToCopy

                _inflightTail += ring.Length; // Update Tail

                break;
            }

            flushable = true;

            if (ring.Length == position)
                break;
        }
            
        // Return the ring to the kernel, at this stage the request was either handled or the ring data
        // has already been copied to the inflight buffer.
        connection.ReturnRing(ring.BufferId);

        return flushable;
    }
    
    private static bool HandleNoInflight(Connection connection, ref ReadOnlySpan<byte> data, out int position)
    {
        // Hotpath, typically each ring contains a full request and inflight buffer isn't used
        position = data.IndexOf("\r\n\r\n"u8);
        var found = position != -1;
        
        if (!found)
        {
            position = 0;
            return false;
        } 

        position += 4;

        var requestSpan = data[..position];
        
        // Handle the request
        // ...
        if(found) WriteResponse(connection); // Simulating writing the response after handling the received request

        return found;
    }

    private static bool HandleWithInflight(Connection connection, UnmanagedMemoryManager[] unmanagedMemories, out int position)
    {
        var sequence = unmanagedMemories.ToReadOnlySequence();
        var reader = new SequenceReader<byte>(sequence);
        var found = reader.TryReadTo(out ReadOnlySequence<byte> headersSequence, "\r\n\r\n"u8);
        
        if (!found)
        {
            position = 0;
            return false;
        } 

        position = reader.Position.GetInteger();
        
        // Handle the request
        // ...
        if(found) WriteResponse(connection); // Simulating writing the response after handling the received request

        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteResponse(Connection connection)
    {
        // Write the response
        var msg =
            "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;

        connection.InnerWrite(msg);
    }
}