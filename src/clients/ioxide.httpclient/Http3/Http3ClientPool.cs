using ioxide.http;
using ioxide.ngtcp2;

namespace ioxide.httpclient;

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
/// using HttpClientResponse response = await upstream.GetAsync("/api/things"u8);
/// </code>
/// </summary>
/// <remarks>
/// Reactor thread only, and it needs no server: the first connection makes the reactor open a UDP
/// socket on an ephemeral port for outbound use. When the app already serves QUIC, outbound
/// connections share that socket instead - either way the replies route back by connection ID.
///
/// The pool deliberately lives on the caller's reactor rather than one of its own, so a handler
/// awaiting an upstream response resumes inline on its own reactor thread. A client with its own
/// thread would have to post continuations back through the synchronization context.
/// </remarks>
public sealed class Http3ClientPool : IDisposable
{
    private readonly Reactor _reactor;
    private readonly Http3ClientOptions _options;
    private readonly QuicClientEngine _engine;
    private readonly List<Http3ClientConnection> _connections = [];
    private int _next;
    private bool _disposed;

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

    public ValueTask<HttpClientResponse> GetAsync(ReadOnlyMemory<byte> path)
        => SendAsync(new HttpClientRequest(HttpMethods.Get, path));

    public ValueTask<HttpClientResponse> GetAsync(string path)
        => SendAsync(new HttpClientRequest(HttpMethods.Get, path));

    public ValueTask<HttpClientResponse> PostAsync(ReadOnlyMemory<byte> path, ReadOnlyMemory<byte> body)
        => SendAsync(new HttpClientRequest(HttpMethods.Post, path) { Body = body });

    /// <summary>Send one request on a live connection; the response owns its bytes.</summary>
    public ValueTask<HttpClientResponse> SendAsync(HttpClientRequest request)
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

        // _next is masked, not just incremented: a bare _next++ wraps negative after ~2^31
        // requests and the modulo then throws on every call.
        _next = (_next + 1) & int.MaxValue;
        return _connections[_next % _connections.Count];
    }

    private void OpenOne()
    {
        try
        {
            QuicEngineConnection quic = _engine.Connect(_reactor, _options.Host, _options.Port, _options.ServerName);
            _connections.Add(new Http3ClientConnection(quic, $"{_options.ServerName}:{_options.Port}", _options.AcquireTimeoutMs));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[httpclient] connect to {_options.Host}:{_options.Port} failed: {e.Message}");
        }
    }

    private void Sweep()
    {
        if (_disposed)
        {
            return;   // AddTicker has no removal API, so a disposed pool must no-op: reopening
                      // here would hand QuicClientEngine.Connect an engine handle we already freed.
        }

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
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Order matters: each connection closes its QUIC connection (and with it the picotls
        // session, which points into the engine) BEFORE the engine itself is freed. Freeing the
        // engine first leaves every live session reading freed memory on its next crypto op.
        foreach (Http3ClientConnection connection in _connections)
        {
            connection.Dispose();
        }
        _connections.Clear();
        _engine.Dispose();
    }
}
