using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using ioxide;
using ioxide.utils;
using Playground.Http;

namespace Playground.Handlers;

/// <summary>
/// The synthetic modes: no I/O beyond the socket, so they measure the engine itself rather than a
/// backend. Each one is the same fixed response reached by a different path through the runtime.
/// </summary>
internal static class RawHandlers
{
    /// <summary>raw - a fixed plaintext response, written straight to the connection.</summary>
    public static Task Raw(Reactor reactor, TcpConnection conn, byte[] response)
        => ConnectionLoop.ServeAsync(conn, new RawResponder(response));

    private readonly struct RawResponder(byte[] response) : ITcpResponder
    {
        public ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
        {
            RequestParser.Drain(conn, snapshot);
            conn.Write(response);
            return conn.FlushAsync();
        }
    }

    /// <summary>
    /// hop - raw, but every request bounces through the thread pool (Task.Yield) first. Exercises
    /// the off-reactor queues and the eventfd wake.
    /// </summary>
    public static Task Hop(Reactor reactor, TcpConnection conn, byte[] response)
        => ConnectionLoop.ServeAsync(conn, new HopResponder(response));

    private readonly struct HopResponder(byte[] response) : ITcpResponder
    {
        public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
        {
            await Task.Yield();   // continuation runs on the thread pool, off the reactor

            RequestParser.Drain(conn, snapshot);
            conn.Write(response);
            await conn.FlushAsync();
        }
    }

    private static int _offReactorSeen;

    /// <summary>
    /// taskrun - raw, but each request awaits a <see cref="Task.Run{TResult}(Func{TResult})"/> JSON
    /// serialization. With the reactor SynchronizationContext installed the continuation comes home
    /// to the reactor; without it, it stays on the thread pool. Logs once if the post-await thread
    /// is off-reactor.
    /// </summary>
    public static Task TaskRun(Reactor reactor, TcpConnection conn)
        => ConnectionLoop.ServeAsync(conn, new TaskRunResponder(reactor));

    private readonly struct TaskRunResponder(Reactor reactor) : ITcpResponder
    {
        public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
        {
            RequestParser.Drain(conn, snapshot);

            string json = await Task.Run(static () => JsonSerializer.Serialize("hello world"));

            if (!reactor.OnReactorThread && Interlocked.Exchange(ref _offReactorSeen, 1) == 0)
            {
                Console.WriteLine("[taskrun] continuation resumed OFF the reactor (no sync context)");
            }

            conn.Write(Responses.JsonHeader);
            conn.Write(Encoding.UTF8.GetBytes(json));
            await conn.FlushAsync();
        }
    }

    /// <summary>
    /// pipe - identical workload to raw, but read and written through the PipeReader/PipeWriter
    /// adapters. Exists to benchmark the adapter overhead against the raw API, so it keeps its own
    /// loop rather than sharing <see cref="ConnectionLoop"/>.
    /// </summary>
    public static async Task Pipe(Reactor reactor, TcpConnection conn, byte[] response)
    {
        var reader = new TcpConnectionPipeReader(conn);
        var writer = new TcpConnectionPipeWriter(conn);

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();

                // Raw mode doesn't parse the request either - consume everything.
                reader.AdvanceTo(result.Buffer.End);

                response.CopyTo(writer.GetSpan(response.Length));
                writer.Advance(response.Length);
                await writer.FlushAsync();

                if (result.IsCompleted) return;
            }
        }
        finally
        {
            reader.Complete();
            conn.DecRef();
        }
    }
}
