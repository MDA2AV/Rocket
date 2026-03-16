using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using zerg;
using Zerg.Core;

namespace Examples.TechEmpower;

/// <summary>
/// Incremental-mode connection handler.
///
/// Same inflight buffer + ReturnRing approach as ConnectionHandler.
/// Each ring item is processed and then returned via ReturnRing so the
/// reactor can decrement the refcount and recycle the buffer once the
/// kernel is also done with it (BufKernelDone).
/// </summary>
internal sealed class ConnectionHandlerIncremental
{
    private readonly unsafe byte* _inflightData;
    private int _inflightTail;
    private readonly int _length;

    // Debug counter
    private int _responsesWritten;

    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    private const string _jsonBody = "Hello, World!";
    private static ReadOnlySpan<byte> s_plainTextBody => "Hello, World!"u8;

    private static ReadOnlySpan<byte> s_headersJson => "HTTP/1.1 200 OK\r\nContent-Length:   \r\nServer: S\r\nContent-Type: application/json\r\n"u8;
    private static ReadOnlySpan<byte> s_headersPlainText => "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nServer: S\r\nContent-Type: text/plain\r\n"u8;

    public unsafe ConnectionHandlerIncremental(int length = 1024 * 16)
    {
        _length = length;
        _inflightData = (byte*)NativeMemory.AlignedAlloc((nuint)_length, 64);
        _inflightTail = 0;
    }

    internal async Task HandleConnectionAsync(Connection connection)
    {
        try
        {
            while (true)
            {
                var result = await connection.ReadAsync();
                if (result.IsClosed)
                {
                    unsafe
                    {
                        string inflightHex = _inflightTail > 0
                            ? System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(_inflightData, _inflightTail))
                            : "";
                        //Console.WriteLine($"[INC] fd={connection.ClientFd} closed: responses={_responsesWritten} inflight={_inflightTail} data=[{inflightHex}]");
                    }
                    break;
                }

                if (HandleResult(connection, ref result))
                {
                    await connection.FlushAsync();
                }

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

    private unsafe bool HandleResult(Connection connection, ref RingSnapshot ringSnapshot)
    {
        bool flushable;
        int advanced;

        UnmanagedMemoryManager[] rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(ringSnapshot);
        int ringCount = rings.Length;

        if (ringCount == 0)
            return false;

        int oldInflightTail = _inflightTail;

        if (_inflightTail == 0)
        {
            flushable = ProcessRings(connection, rings, out advanced, ref _responsesWritten);
        }
        else
        {
            // Cold path
            UnmanagedMemoryManager[] mems = new UnmanagedMemoryManager[ringCount + 1];

            mems[0] = new(_inflightData, _inflightTail);

            for (int i = 1; i < ringCount + 1; i++)
                mems[i] = rings[i - 1];

            flushable = ProcessRings(connection, mems, out advanced, ref _responsesWritten);

            if (flushable)
                _inflightTail = 0;
        }

        if (!flushable)
        {
            // No complete request found. Copy ring data to the inflight buffer
            // and return rings.
            int totalNew = CalculateRingsTotalLength(rings);
            //Console.WriteLine($"[INC] no-flush: rings={ringCount} totalBytes={totalNew} inflightBefore={oldInflightTail} inflightAfter={_inflightTail + totalNew}");
            for (int i = 0; i < rings.Length; i++)
            {
                Buffer.MemoryCopy(
                    rings[i].Ptr,
                    _inflightData + _inflightTail,
                    _length - _inflightTail,
                    rings[i].Length);
                _inflightTail += rings[i].Length;
            }

            for (int i = 0; i < rings.Length; i++)
                connection.ReturnRing(rings[i].BufferId);

            return false;
        }

        // When inflight data was prepended, advanced includes those bytes.
        // Subtract them so advanced is relative to rings only.
        int ringAdvanced = advanced - oldInflightTail;
        int ringsTotalLength = CalculateRingsTotalLength(rings);

        if (ringAdvanced < ringsTotalLength)
        {
            var currentRingIndex = GetCurrentRingIndex(in ringAdvanced, rings, out var currentRingAdvanced);

            // Copy current ring unused data
            Buffer.MemoryCopy(
                rings[currentRingIndex].Ptr + currentRingAdvanced,
                _inflightData + _inflightTail,
                _length - _inflightTail,
                rings[currentRingIndex].Length - currentRingAdvanced);

            _inflightTail += rings[currentRingIndex].Length - currentRingAdvanced;

            // Copy untouched rings data
            for (int i = currentRingIndex + 1; i < rings.Length; i++)
            {
                Buffer.MemoryCopy(
                    rings[i].Ptr,
                    _inflightData + _inflightTail,
                    _length - _inflightTail,
                    rings[i].Length);

                _inflightTail += rings[i].Length;
            }
        }

        // Return all rings — data has been processed or copied to inflight.
        // The reactor will decrement refcount and recycle the buffer once
        // both the handler and kernel are done with it.
        for (int i = 0; i < rings.Length; i++)
            connection.ReturnRing(rings[i].BufferId);

        return flushable;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool ProcessRings(Connection connection, UnmanagedMemoryManager[] rings, out int advanced, ref int responseCount)
    {
        advanced = 0;

        int idx;
        bool flushable = false;

        //Console.WriteLine(rings.Length);
        
        ReadOnlySpan<byte> data = rings.Length == 1
            ? new ReadOnlySpan<byte>(rings[0].Ptr, rings[0].Length)
            : rings.ToReadOnlySequence().ToArray();
        
        //Console.WriteLine($"{Encoding.UTF8.GetString(data)}");

        while (true)
        {
            idx = data.IndexOf("\r\n\r\n"u8);
            if (idx == -1) return flushable;

            int idx4 = idx + 4;
            advanced += idx4;
            int space1 = data.IndexOf((byte)' ');
            if (space1 == -1) return flushable;
            int space2 = data[(space1 + 1)..].IndexOf((byte)' ');
            if (space2 <= 0) return flushable;

            ReadOnlySpan<byte> route = data[(space1 + 1)..(space1 + 1 + space2)];

            WriteResponse(connection, route[1] == (byte)'j');
            responseCount++;
            flushable = true;
            if (idx4 >= data.Length) break;

            data = data[idx4..];
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteResponse(Connection connection, bool json)
    {
        var tail = connection.WriteTail;
        int contentLength;

        if (json)
        {
            connection.Write(s_headersJson);
            connection.Write(DateHelper.HeaderBytes);

            var utf8JsonWriter = t_writer ??= new Utf8JsonWriter(connection, new JsonWriterOptions { SkipValidation = true });
            utf8JsonWriter.Reset(connection);
            JsonSerializer.Serialize(utf8JsonWriter, new JsonMessage { Message = _jsonBody }, JsonContext.Default.JsonMessage);

            contentLength = (int)utf8JsonWriter.BytesCommitted;

            unsafe
            {
                byte* dst = connection.WriteBuffer + tail + 33;
                int tens = contentLength / 10;
                int ones = contentLength - tens * 10;

                dst[0] = (byte)('0' + tens);
                dst[1] = (byte)('0' + ones);
            }
        }
        else
        {
            connection.Write(s_headersPlainText);
            connection.Write(DateHelper.HeaderBytes);
            connection.Write(s_plainTextBody);
        }
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
