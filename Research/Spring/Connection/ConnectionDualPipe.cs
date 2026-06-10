using System.IO.Pipelines;

namespace Spring;

public sealed class ConnectionDualPipe : IDuplexPipe
{
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    public ConnectionDualPipe(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // Kestrel mode: read through the BCL Pipe the reactor feeds. Raw mode: the IVTS reader.
        Input = connection.InputPipe is { } pipe
            ? pipe.Reader
            : new ConnectionPipeReader(connection);
        Output = new ConnectionPipeWriter(connection);
    }
}