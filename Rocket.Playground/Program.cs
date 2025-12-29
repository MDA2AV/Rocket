using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using URocket;
using URocket.Engine;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

namespace Overdrive;

[SkipLocalsInit]
internal static class Program {
    internal static async Task Main() {
        InitOk();
        var engine = new Engine();
        _ = Task.Run(() => engine.Run());

        try {
            while (true) {
                var conn = await engine.AcceptAsync();
                Console.WriteLine($"Connection: {conn.Fd}");
                _ = HandleAsync(conn);
            }
        } finally { FreeOk(); }
    }
    
    // TODO : Check bug - run wrk pipeline with InitOK (only 1 response flushed) and then run wrk pipeline again
    // TODO : Possibly related with Clear()
    
    private static async ValueTask HandleAsync(Connection connection) {
        try {
            var reactor = connection.Reactor;
            while (true) {
                await connection.ReadAsync();
                unsafe {
                    var span = new ReadOnlySpan<byte>(connection.InPtr, connection.InLength);
                    var s = Encoding.UTF8.GetString(span);
                }
                
                unsafe {
                    if (connection.HasBuffer) {
                        reactor.ReturnBufferRing(connection.InPtr, connection.BufferId);
                    }
                }
                connection.ResetRead();
                unsafe {
                    connection.OutPtr  = OK_PTR;
                    connection.OutHead = 0;
                    connection.OutTail = OK_LEN;

                    // TODO: SubmitSend to reactor class?
                    Engine.SubmitSend(
                        reactor.Ring,
                        connection.Fd,
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
    
    private static unsafe void InitOk()
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

    private static unsafe void FreeOk() {
        if (OK_PTR != null) {
            NativeMemory.Free(OK_PTR);
            OK_PTR = null;
            OK_LEN = 0;
        }
    }
}