using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using URocket.Connection;
using URocket.Utils;
using URocket.Utils.UnmanagedMemoryManager;

namespace Examples.ZeroAlloc.Advanced;

internal sealed class ZeroAlloc_Advanced_MultiRings_ConnectionHandler
{
    private readonly unsafe byte* _inflightData;
    private int _inflightTail;
    private readonly int _length;

    public unsafe ZeroAlloc_Advanced_MultiRings_ConnectionHandler(int length = 1024 * 16)
    {
        _length = length;
        
        // Allocating an unmanaged byte slab to store inflight data
        _inflightData = (byte*)NativeMemory.AlignedAlloc((nuint)_length, 64);

        _inflightTail = 0;
    }

    // Zero allocation read and write example
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

                if (HandleResult(connection, ref result))
                {
                    await connection.InnerFlushAsync(); // Mark data to be ready to be flushed
                }

                // Reset connection's ManualResetValueTaskSourceCore<ReadResult>
                connection.ResetRead();
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Exception --: {e}");
        }
        finally
        {
            unsafe { NativeMemory.AlignedFree(_inflightData); }
        }
    }

    private unsafe bool HandleResult(Connection connection, ref ReadResult result)
    {
        // Signals where at least one request was handled, and we can flush written response data
        var flushable = false;

        var totalAdvanced = 0;

        // Get the "head" ring - first ring received
        var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
        var ringsTotalLength = CalculateRingsTotalLength(rings);
        var ringCount = rings.Length;
        
        if(ringCount == 0)
            return false;
        
        while (true) // Inner loop, iterates every request handling, it can happen 0, 1 or n times
            // per read as we may read an incomplete, one single or multiple requests at once
        {
            bool found;
            int advanced;
            // Here we want to merge existing inflight data with the just read new data
            if (_inflightTail == 0)
            {
                if (ringCount == 1)
                {
                    // Very Hot Path, typically each ring contains a full request and inflight buffer isn't used
                    var span = new ReadOnlySpan<byte>(rings[0].Ptr + totalAdvanced, rings[0].Length - totalAdvanced);
                    found = HandleNoInflightSingleRing(connection, ref span, out advanced);
                }
                else
                {
                    // Lukewarm Path
                    found = HandleNoInflightMultipleRings(connection, rings, out advanced);
                }
            }
            else
            {
                // Cold path
                UnmanagedMemoryManager[] mems = new UnmanagedMemoryManager[ringCount + 1];
                mems[0] = new(_inflightData, _inflightTail);
                for (int i = 1; i < ringCount + 1; i++) mems[i] = rings[i];
                
                found = HandleWithInflight(connection, mems, out advanced);

                if (found)  // a request was handled so inflight data can be discarded
                    _inflightTail = 0;
            }

            totalAdvanced += advanced;

            var currentRingIndex = GetCurrentRingIndex(in totalAdvanced, rings, out var currentRingAdvanced);

            if (!found)
            {
                // \r\n\r\n not found, full headers are not yet available
                // Copy the leftover rings data to the inflight buffer and read more
                
                // Copy current ring unused data
                Buffer.MemoryCopy(
                    rings[currentRingIndex].Ptr + currentRingAdvanced, // source
                    _inflightData + _inflightTail, // destination
                    _length - _inflightTail, // destinationSizeInBytes
                    rings[currentRingIndex].Length - currentRingAdvanced); // sourceBytesToCopy
                
                _inflightTail += rings[currentRingIndex].Length - currentRingAdvanced;
                
                // Copy untouched rings data
                for (int i = currentRingIndex + 1; i < rings.Length; i++)
                {
                    Buffer.MemoryCopy(
                        rings[i].Ptr, // source
                        _inflightData + _inflightTail, // destination
                        _length - _inflightTail, // destinationSizeInBytes
                        rings[i].Length); // sourceBytesToCopy
                    
                    _inflightTail += rings[i].Length;
                }

                break;
            }

            flushable = true;
            
            if (ringsTotalLength == advanced)
                break;
        }
            
        // Return the rings to the kernel, at this stage the request was either handled or the rings' data
        // has already been copied to the inflight buffer.
        for (int i = 0; i < rings.Length; i++) 
            connection.ReturnRing(rings[i].BufferId);

        return flushable;
    }
    
    private static bool HandleNoInflightSingleRing(Connection connection, ref ReadOnlySpan<byte> data, out int advanced)
    {
        // Hotpath, typically each ring contains a full request and inflight buffer isn't used
        advanced = data.IndexOf("\r\n\r\n"u8);
        var found = advanced != -1;

        if (!found)
        {
            advanced = 0;
            return false;
        }
        
        advanced += 4;

        var requestSpan = data[..advanced];
        
        // Handle the request
        // ...
        if(found) WriteResponse(connection); // Simulating writing the response after handling the received request

        return found;
    }

    private static bool HandleNoInflightMultipleRings(Connection connection, UnmanagedMemoryManager[] rings, out int position)
    {
        var sequence = rings.ToReadOnlySequence();
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

    private static bool HandleWithInflight(Connection connection, UnmanagedMemoryManager[] unmanagedMemories,
        out int position)
    {
        var sequence = unmanagedMemories.ToReadOnlySequence();
        var reader = new SequenceReader<byte>(sequence);
        var found = reader.TryReadTo(out ReadOnlySequence<byte> headersSequence, "\r\n\r\n"u8);

        if (!found)
        {
            position = 0;
            return false;
        }

        // Calculating how many bytes from the received rings were consumed
        // inflight data is subtracted
        position = reader.Position.GetInteger() - unmanagedMemories[0].Length;

        // Handle the request
        // ...
        if(found) WriteResponse(connection); // Simulating writing the response after handling the received request

        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteResponse(Connection connection)
    {
        // Write the response
        var msg =
            "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;

        connection.InnerWrite(msg);
    }
    
    
    private static int GetCurrentRingIndex(in int totalAdvanced, UnmanagedMemoryManager[] rings, out int currentRingAdvanced)
    {
        var total = 0;

        for (int i = 0; i < rings.Length; i++)
        {
            if (rings[i].Length + total >= totalAdvanced)
            {
                currentRingAdvanced = totalAdvanced - total;
                return i;
            }
            
            total += rings[i].Length;
        }

        currentRingAdvanced = -1;
        return -1;
    }

    private static int CalculateRingsTotalLength(UnmanagedMemoryManager[] rings)
    {
        var total = 0;
        for (int i = 0; i < rings.Length; i++) total += rings[i].Length;
        return total;
    }
}