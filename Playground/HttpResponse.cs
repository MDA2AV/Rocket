using System.Runtime.InteropServices;
using System.Text;
using URocket;

namespace Playground;

public class HttpResponse
{
    // TODO : Check bug - run wrk pipeline with InitOK (only 1 response flushed) and then run wrk pipeline again
    // TODO : Possibly related with Clear()
    
    internal static async ValueTask HandleAsync(Connection connection) {
        try {
            var reactor = connection.Reactor;
            while (true) {
                await connection.ReadAsync();

                Console.WriteLine($"{connection.Reactor.Id} Request received");
                
                unsafe {
                    var span = new ReadOnlySpan<byte>(connection.InPtr, connection.InLength);
                    //var s = Encoding.UTF8.GetString(span);
                }
                
                unsafe {
                    if (connection.HasBuffer) {
                        reactor.ReturnBufferRing(connection.InPtr, connection.BufferId);
                    }
                }
                
                Console.WriteLine($"{connection.Reactor.Id} Calling reset..");
                connection.ResetRead();
                unsafe {
                    connection.OutPtr  = OK_PTR;
                    connection.OutHead = 0;
                    connection.OutTail = OK_LEN;
                    
                    reactor.SubmitSend(
                        reactor.Ring,
                        connection.ClientFd,
                        connection.OutPtr,
                        connection.OutHead,
                        connection.OutTail);
                }
            }
        } catch (Exception e) { Console.WriteLine(e); }
        Console.WriteLine("end");
    }

    private static unsafe byte* OK_PTR;
    private static nuint OK_LEN;

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
        nuint oneLen = (nuint)one.Length;

        OK_LEN = oneLen * (nuint)pipeline;
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
        OK_LEN = (nuint)a.Length;
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