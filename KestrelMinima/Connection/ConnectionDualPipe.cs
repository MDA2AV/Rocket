using System.IO.Pipelines;

namespace KestrelMinima;

public sealed class ConnectionDualPipe : IDuplexPipe
{
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    public ConnectionDualPipe(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // Kestrel mode only — InitInputPipe is always called on accept.
        Input = connection.InputPipe!.Reader;
        Output = new ConnectionPipeWriter(connection);
    }
}