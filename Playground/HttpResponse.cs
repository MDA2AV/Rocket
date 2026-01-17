using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using URocket;
using URocket.Connection;
using URocket.Engine;
using URocket.Utils;
using URocket.Utils.ReadOnlySequence;
using URocket.Utils.ReadOnlySpan;
using URocket.Utils.UnmanagedMemoryManager;

namespace Playground;

public class HttpResponse
{
    internal static async ValueTask HandleAsync(Connection connection) {
        try {
            while (true) {
                var result = await connection.ReadAsync();
                if (result.IsClosed)
                    break;
                
                /*
                if (connection.RingCount == 1) {
                    connection.TryDequeueBatch(result.TailSnapshot, out var item);
                    
                    var mem = item.AsUnmanagedMemoryManager();
                    var idx = mem.GetSpan().IndexOf("\r\n\r\n"u8);
                    if(idx == -1)
                        Console.WriteLine("-1");

                    // Return buffer after you’re done with that segment
                    connection.Reactor.EnqueueReturnQ(item.BufferId);
                    
                } else {
                    while (connection.TryDequeueBatch(result.TailSnapshot, out var item)) {
                        var mem = item.AsUnmanagedMemoryManager();
                        var idx = mem.GetSpan().IndexOf("\r\n\r\n"u8);
                        if(idx == -1)
                            Console.WriteLine("-1");

                        // Return buffer after you’re done with that segment
                        connection.Reactor.EnqueueReturnQ(item.BufferId);
                    }
                }
                */

                var mems = Extract(connection, result);
                var seq = mems.ToReadOnlySequence();
                var reader = new SequenceReader<byte>(seq);

                if (reader.TryReadTo(out ReadOnlySequence<byte> headers, "\r\n\r\n"u8)) {
                    // parse request
                    
                    if(reader.End)
                        mems.ReturnRingBuffers(connection.Reactor);
                    else
                    {
                        
                    }
                } else {
                    Console.WriteLine("665123");
                    // Not enough data, wait for more

                    connection.ResetRead();
                    continue;
                }
                
                var msg = "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;
                if (!connection.Reactor.TryEnqueueWrite(new WriteItem(msg.ToUnmanagedMemoryManager(),
                        connection.ClientFd))) 
                {
                    throw new InvalidOperationException("Failed to write response");
                }
                
                connection.Flushable = true;
                connection.ResetRead();
            }
        } catch (Exception e) { Console.WriteLine(e); }
        Console.WriteLine("end");
    }

    private static UnmanagedMemoryManager[] Extract(Connection connection, ReadResult readResult) {
        var count = connection.RingCount;

        if (count == 1) {
            connection.TryDequeueBatch(readResult.TailSnapshot, out var item);
            return [item.AsUnmanagedMemoryManager()];
        }
        
        var mems = new UnmanagedMemoryManager[count];
        for (int i = 0; i < count; i++) {
            connection.TryDequeueBatch(readResult.TailSnapshot, out var item);
            mems[i] = item.AsUnmanagedMemoryManager();
        }
        
        return mems;
    }

    private static unsafe byte* OK_PTR;
    private static uint OK_LEN;

    private static unsafe void InitPipelineOk(int pipeline = 16)
    {
        const string response =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 13\r\n" +
            "Connection: keep-alive\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Hello, World!";

        // Encode once
        byte[] one = Encoding.ASCII.GetBytes(response); // ASCII is enough here
        uint oneLen = (uint)one.Length;

        OK_LEN = oneLen * (uint)pipeline;
        OK_PTR = (byte*)NativeMemory.Alloc(OK_LEN);

        // Copy first response
        fixed (byte* src = one)
        {
            Buffer.MemoryCopy(src, OK_PTR, OK_LEN, oneLen);

            // Exponentially double-copy: O(log N) memcpy calls
            nuint filled = oneLen;
            while (filled < OK_LEN)
            {
                nuint copy = (filled <= (OK_LEN - filled)) ? filled : (OK_LEN - filled);
                Buffer.MemoryCopy(OK_PTR, OK_PTR + filled, OK_LEN - filled, copy);
                filled += copy;
            }
        }
    }
    
    internal static unsafe void InitOk()
    {
        var s =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 13\r\n" +
            "Connection: keep-alive\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Hello, World!";
        var a = Encoding.UTF8.GetBytes(s);
        OK_LEN = (uint)a.Length;
        OK_PTR = (byte*)NativeMemory.Alloc(OK_LEN);
        for (int i = 0; i < a.Length; i++)
            OK_PTR[i] = a[i];
    }

    internal static unsafe void FreeOk() {
        if (OK_PTR != null) {
            NativeMemory.Free(OK_PTR);
            OK_PTR = null;
            OK_LEN = 0;
        }
    }
}