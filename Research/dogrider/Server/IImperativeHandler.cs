namespace dogrider.Server;

/// <summary>
/// Application-side message pump. After handshake, dogrider invokes <see cref="HandleAsync"/>
/// once per accepted WebSocket connection on a thread-pool task. The handler owns the read loop:
/// it calls <see cref="IImperativeConnection.ReadFrameAsync"/> until it sees a close (or returns).
/// </summary>
public interface IImperativeHandler
{
    ValueTask HandleAsync(IImperativeConnection connection);
}
