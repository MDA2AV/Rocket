using System.IO.Pipelines;

namespace ioxide;

public sealed class ConnectionDualPipe : IDuplexPipe
{
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    public ConnectionDualPipe(TcpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Input = new ConnectionPipeReader(connection);
        Output = new ConnectionPipeWriter(connection);
    }
}