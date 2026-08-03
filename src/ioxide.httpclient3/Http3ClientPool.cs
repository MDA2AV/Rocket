using ioxide.ngtcp2;

namespace ioxide.httpclient3;

/// <summary>Origin and connection settings for <see cref="Http3ClientPool"/>.</summary>
public sealed record Http3ClientOptions
{
    /// <summary>IPv4 literal (DNS would block the reactor).</summary>
    public string Host { get; init; } = "127.0.0.1";

    public ushort Port { get; init; } = 443;

    /// <summary>SNI / :authority value sent to the origin.</summary>
    public string ServerName { get; init; } = "localhost";

    /// <summary>QUIC connections to keep open. Each multiplexes many concurrent requests, so this
    /// is far smaller than an HTTP/1.1 pool - one is often enough.</summary>
    public int PoolSize { get; init; } = 1;

    /// <summary>How long a request waits for a usable connection (handshake included).</summary>
    public int AcquireTimeoutMs { get; init; } = 10_000;
}

/// <summary>
/// HTTP/3 connections to one origin on a single reactor's ring. Unlike the HTTP/1.1 pool, a
/// connection is not checked out per request - h3 multiplexes, so requests round-robin across the
/// live connections and each one carries many streams at once.
///
/// <code>
/// reactor.OnStart = r => Http3ClientPool.Start(r, new Http3ClientOptions { Host = "10.0.0.7", Port = 8443 });
/// ...
/// var upstream = reactor.GetService&lt;Http3ClientPool&gt;();
/// using Http3ClientResponse response = await upstream.GetAsync("/api/things"u8);
/// </code>
/// </summary>
/// <remarks>
/// Reactor thread only. The reactor must have QUIC configured - client connections ride its QUIC
/// socket, which is also what routes the replies back.
/// </remarks>
public sealed class Http3ClientPool : IDisposable
{
    private readonly Reactor _reactor;
    private readonly Http3ClientOptions _options;
    private readonly QuicClientEngine _engine;
    private readonly List<Http3ClientConnection> _connections = [];
    private int _next;

    private Http3ClientPool(Reactor reactor, Http3ClientOptions options)
    {
        _reactor = reactor;
        _options = options;
        _engine = new QuicClientEngine("h3");
    }

    /// <summary>Open the connections and register the pool as a reactor service.</summary>
    public static Http3ClientPool Start(Reactor reactor, Http3ClientOptions options)
    {
        var pool = new Http3ClientPool(reactor, options);
        for (int i = 0; i < options.PoolSize; i++)
        {
            pool.OpenOne();
        }
        reactor.AddService(pool);
        reactor.AddTicker(pool.Sweep);
        return pool;
    }

    /// <summary>Live connections (for diagnostics).</summary>
    public int ConnectionCount => _connections.Count;

    public ValueTask<Http3ClientResponse> GetAsync(ReadOnlyMemory<byte> path)
        => SendAsync(new Http3ClientRequest(Http3Methods.Get, path));

    public ValueTask<Http3ClientResponse> GetAsync(string path)
        => SendAsync(new Http3ClientRequest(Http3Methods.Get, path));

    public ValueTask<Http3ClientResponse> PostAsync(ReadOnlyMemory<byte> path, ReadOnlyMemory<byte> body)
        => SendAsync(new Http3ClientRequest(Http3Methods.Post, path) { Body = body });

    /// <summary>Send one request on a live connection; the response owns its bytes.</summary>
    public ValueTask<Http3ClientResponse> SendAsync(Http3ClientRequest request)
    {
        Http3ClientConnection connection = Pick()
            ?? throw new Http3ClientException($"no HTTP/3 connection to {_options.Host}:{_options.Port}");

        return connection.SendAsync(request);
    }

    // Round-robin over the healthy connections, dropping corpses on the way past.
    private Http3ClientConnection? Pick()
    {
        for (int i = _connections.Count - 1; i >= 0; i--)
        {
            if (_connections[i].IsBroken)
            {
                _connections[i].Dispose();
                _connections.RemoveAt(i);
            }
        }

        if (_connections.Count == 0)
        {
            OpenOne();
            return _connections.Count > 0 ? _connections[0] : null;
        }

        return _connections[_next++ % _connections.Count];
    }

    private void OpenOne()
    {
        try
        {
            QuicEngineConnection quic = _engine.Connect(_reactor, _options.Host, _options.Port, _options.ServerName);
            _connections.Add(new Http3ClientConnection(quic, $"{_options.ServerName}:{_options.Port}"));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[httpclient3] connect to {_options.Host}:{_options.Port} failed: {e.Message}");
        }
    }

    private void Sweep()
    {
        for (int i = _connections.Count - 1; i >= 0; i--)
        {
            if (_connections[i].IsBroken)
            {
                _connections[i].Dispose();
                _connections.RemoveAt(i);
            }
        }

        if (_connections.Count < _options.PoolSize)
        {
            OpenOne();
        }
    }

    public void Dispose()
    {
        foreach (Http3ClientConnection connection in _connections)
        {
            connection.Dispose();
        }
        _connections.Clear();
        _engine.Dispose();
    }
}
