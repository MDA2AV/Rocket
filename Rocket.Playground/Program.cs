using System.Runtime.CompilerServices;
using System.Text;
using Rocket;
using Rocket.Engine;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

namespace Overdrive;

[SkipLocalsInit]
internal static class Program {
    internal static async Task Main() {
        var builder = RocketEngine
            .CreateBuilder()
            .ReactorQuant(() => Environment.ProcessorCount/4)
            .Backlog(16 * 1024)
            .Port(8080)
            .RecvBufferSize(32 * 1024);
        
        var engine = builder.Build();
        _ = Task.Run(() => engine.Run());
        
        while (true) {
            var conn = await engine.AcceptAsync();
            Console.WriteLine($"Connection: {conn.Fd}");

            _ = HandleAsync(conn);
        }
    }

    private static async ValueTask HandleAsync(Connection connection) {
        try {
            while (true) {
                await connection.ReadAsync();
                unsafe {
                    var span = new ReadOnlySpan<byte>(connection.InPtr, connection.InLength);
                    var s = Encoding.UTF8.GetString(span);
                }
                
                unsafe {
                    if (connection.HasBuffer) {
                        var worker = RocketEngine.s_Reactors[connection.ReactorId];
                        worker.ReturnBufferRing(connection.InPtr, connection.BufferId);
                    }
                }
                connection.ResetRead();
                unsafe {
                    connection.OutPtr  = RocketEngine.OK_PTR;
                    connection.OutHead = 0;
                    connection.OutTail = RocketEngine.OK_LEN;

                    RocketEngine.SubmitSend(
                        RocketEngine.s_Reactors[connection.ReactorId].PRing,
                        connection.Fd,
                        connection.OutPtr,
                        connection.OutHead,
                        connection.OutTail);
                }
            }
        }catch (Exception e) { Console.WriteLine(e); }
        Console.WriteLine("end");
    }
}