using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;

namespace Raptor;

internal sealed class RaptorConnectionListener : IConnectionListener
{
    private readonly RaptorEngine _engine;

    public RaptorConnectionListener(RaptorEngine engine, EndPoint endpoint)
    {
        _engine = engine;
        EndPoint = endpoint;
    }

    public EndPoint EndPoint { get; }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            RaptorConnection conn = await _engine.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new RaptorConnectionContext(conn, EndPoint);
        }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException)     { return null; }
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _engine.Stop();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _engine.Stop();
        return ValueTask.CompletedTask;
    }
}
