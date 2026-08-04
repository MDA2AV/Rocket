using System.IO.Pipelines;
using ioxide;
using ioxide.utils;
using Playground.Shared;
using Playground.Shared.Http;

// pipe - the same workload as the raw sample, but read and written through the PipeReader/PipeWriter
// adapters. It exists to price the adapter against the raw API, so it deliberately keeps its own
// loop instead of sharing ConnectionLoop: the loop is part of what is being measured.

int bodyBytes = Responses.FixedBodyBytesFromEnvironment();
byte[] response = Responses.BuildFixedOk(bodyBytes);

return PlaygroundHost.Run(new PlaygroundSample
{
    Name = "pipe",
    Summary = "raw workload through the PipeReader/PipeWriter adapters",
    Tcp = async (reactor, conn) =>
    {
        var reader = new TcpConnectionPipeReader(conn);
        var writer = new TcpConnectionPipeWriter(conn);

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();

                // The raw sample doesn't parse the request either - consume everything.
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
    },
});
