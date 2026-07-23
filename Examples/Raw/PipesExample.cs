using System.Buffers;
using ioxide;

namespace Examples.Raw;

/// <summary>
/// Raw read and flush through the PipeReader/PipeWriter adapters, no database - the workload
/// for measuring adapter overhead against the raw API.
/// </summary>
public static class PipesExample
{
    public static async Task Handle(Reactor r, TcpConnection conn)
    {
        var reader = new TcpConnectionPipeReader(conn);
        var writer = new TcpConnectionPipeWriter(conn);

        while (true)
        {
            var result = await reader.ReadAsync();

            // Consume everything - this workload doesn't parse.
            reader.AdvanceTo(result.Buffer.End);

            writer.Write(Http.OkResponse);
            await writer.FlushAsync();

            if (result.IsCompleted)
            {
                reader.Complete();
                writer.Complete();
                conn.DecRef();
                return;
            }
        }
    }
}
