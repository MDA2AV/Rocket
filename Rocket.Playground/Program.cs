using System.Runtime.CompilerServices;
using System.Text;
using Rocket.Engine;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

namespace Overdrive;

[SkipLocalsInit]
internal static class Program
{
    internal static async Task Main()
    {
        var builder = RocketEngine
            .CreateBuilder()
            .SetWorkersSolver(() => 32)
            .SetBacklog(16 * 1024)
            .SetPort(8080)
            .SetRecvBufferSize(32 * 1024);
        
        var engine = builder.Build();
        _ = Task.Run(() => engine.Run());
        
        while (true)
        {
            var conn = await engine.AcceptAsync();
            Console.WriteLine($"Connection: {conn.Fd}");

            _ = HandleAsync(conn);
        }
    }
    
    internal static async ValueTask HandleAsync(Connection connection)
    {
        try
        {
            while (true)
            {
                // 1) Wait for worker to signal bytes are ready
                await connection.ReadAsync();

                // 2) Consume bytes BEFORE resetting anything
                unsafe
                {
                    var span = new ReadOnlySpan<byte>(connection.InPtr, connection.InLength);
                    // Avoid decoding for perf; just showing correctness
                    var s = Encoding.UTF8.GetString(span);
                }

                // 3) Return buf-ring buffer (important!)
                unsafe
                {
                    if (connection.HasBuffer)
                    {
                        var worker = RocketEngine.s_Workers[connection.WorkerIndex];
                        worker.ReturnBufferRing(connection.InPtr, connection.BufferId);
                    }
                }

                // 4) Now reset the read cycle so you can arm the next ReadAsync()
                connection.ResetRead();

                // 5) Send response
                unsafe
                {
                    connection.OutPtr  = RocketEngine.OK_PTR;
                    connection.OutHead = 0;
                    connection.OutTail = RocketEngine.OK_LEN;

                    RocketEngine.SubmitSend(
                        RocketEngine.s_Workers[connection.WorkerIndex].PRing,
                        connection.Fd,
                        connection.OutPtr,
                        connection.OutHead,
                        connection.OutTail);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        Console.WriteLine("end");
    }

    
    internal static async ValueTask HandleAsync2(Connection connection)
    {
        try
        {
            while (true)
            {
                // Read request
                await connection.ReadAsync();
                //connection.Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                //connection.Tcs = new(); // reset tcs
                connection.ResetRead();

                unsafe
                {
                    var span = new ReadOnlySpan<byte>(connection.InPtr, connection.InLength);
                    var s = Encoding.UTF8.GetString(span);
                    //Console.WriteLine(s[0]);
                }

                // Flush response
                unsafe
                {
                    if (connection.HasBuffer)
                    {
                        var worker = RocketEngine.s_Workers[connection.WorkerIndex];
                        worker.ReturnBufferRing(connection.InPtr, connection.BufferId);
                    }

                    var okPtr = RocketEngine.OK_PTR;
                    var okLen = RocketEngine.OK_LEN;
                        
                    connection.OutPtr  = okPtr;
                    connection.OutHead = 0;
                    connection.OutTail = okLen;
                    
                    RocketEngine.SubmitSend(
                        RocketEngine.s_Workers[connection.WorkerIndex].PRing,
                        connection.Fd,
                        connection.OutPtr,
                        connection.OutHead,
                        connection.OutTail);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        Console.WriteLine("end");
    }
}