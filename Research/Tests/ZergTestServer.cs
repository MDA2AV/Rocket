using System.Net;
using System.Net.Sockets;
using zerg;
using zerg.Engine;
using zerg.Engine.Configs;

namespace Tests;

/// <summary>
/// Spins up a real zerg Engine on a random available port.
/// Accepts connections and dispatches them to the provided handler.
/// </summary>
public sealed class ZergTestServer : IAsyncDisposable
{
    public Engine Engine { get; }
    public int Port { get; }

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public ZergTestServer(Func<Connection, Task> handler, int reactorCount = 1, ReactorConfig? reactorConfig = null)
    {
        Port = GetAvailablePort();

        Engine = new Engine(new EngineOptions
        {
            Ip = "0.0.0.0",
            Port = (ushort)Port,
            ReactorCount = reactorCount,
            Backlog = 128,
            AcceptorConfig = new AcceptorConfig(IPVersion: IPVersion.IPv4Only),
            ReactorConfigs = reactorConfig != null
                ? Enumerable.Range(0, reactorCount).Select(_ => reactorConfig).ToArray()
                : null
        });

        Engine.Listen();

        _acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (Engine.ServerRunning)
                {
                    var connection = await Engine.AcceptAsync(_cts.Token);
                    if (connection is null) continue;
                    _ = handler(connection);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        Engine.Stop();
        _cts.Cancel();

        try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* timeout or cancelled, that's fine */ }

        _cts.Dispose();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
