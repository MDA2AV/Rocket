using System.IO.Pipelines;

namespace zerg.core;

public sealed class ConnectionDualPipe : IDuplexPipe
{
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    public ConnectionDualPipe(ConnectionBase connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Input = new ConnectionPipeReader(connection);
        Output = new ConnectionPipeWriter(connection);
    }
}
