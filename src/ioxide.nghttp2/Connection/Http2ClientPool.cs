using ioxide.httpclient;

namespace ioxide.nghttp2;

/// <summary>Origin and connection settings for <see cref="Http2ClientPool"/>.</summary>
public sealed record Http2ClientOptions
{
    /// <summary>IPv4 literal (DNS would block the reactor).</summary>
    public required string Host { get; init; }

    public required ushort Port { get; init; }

    /// <summary>
    /// Connections to hold open. HTTP/2 multiplexes, so one connection carries many concurrent
    /// requests - more connections buy parallelism only past the peer's MAX_CONCURRENT_STREAMS.
    /// </summary>
    public int PoolSize { get; init; } = 1;

    public int AcquireTimeoutMs { get; init; } = 10_000;
}

/// <summary>
/// HTTP/2 connections to one origin, on one reactor's ring. Requests are multiplexed across the
/// pool round-robin; a broken connection is dropped and replaced in the background.
/// </summary>
/// <remarks>Reactor thread only - create it from <c>Reactor.OnStart</c>.</remarks>
public sealed class Http2ClientPool : IDisposable
{
    private readonly IRingHost _host;
    private readonly Http2ClientOptions _options;
    private readonly string _authority;
    private readonly List<Http2ClientConnection> _connections = [];

    private int _next;
    private int _opening;
    private bool _disposed;

    private Http2ClientPool(IRingHost host, Http2ClientOptions options)
    {
        _host = host;
        _options = options;
        _authority = options.Port is 80 or 443 ? options.Host : $"{options.Host}:{options.Port}";
    }

    /// <summary>Open <see cref="Http2ClientOptions.PoolSize"/> connections and register the pool as
    /// a reactor service.</summary>
    public static Http2ClientPool Start(Reactor reactor, Http2ClientOptions options)
    {
        var pool = new Http2ClientPool(reactor, options);
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

    /// <summary>Send one request on a pooled connection. The response owns its bytes - dispose it.</summary>
    public ValueTask<HttpClientResponse> SendAsync(HttpClientRequest request)
    {
        Http2ClientConnection? connection = Pick();
        if (connection is null)
        {
            throw new Http2ClientException($"no HTTP/2 connection to {_options.Host}:{_options.Port}");
        }
        return connection.SendAsync(request);
    }

    private Http2ClientConnection? Pick()
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
            return null;
        }

        // Masked, not a bare increment: that wraps negative after ~2^31 requests and the modulo
        // then throws on every call.
        _next = (_next + 1) & int.MaxValue;
        return _connections[_next % _connections.Count];
    }

    private void OpenOne()
    {
        _opening++;
        _ = OpenOneAsync();
    }

    private async Task OpenOneAsync()
    {
        try
        {
            Http2ClientConnection connection = await Http2ClientConnection.ConnectAsync(
                _host, _options.Host, _options.Port, _authority);

            if (_disposed)
            {
                connection.Dispose();
                return;
            }
            _connections.Add(connection);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[nghttp2] connect to {_options.Host}:{_options.Port} failed: {e.Message}");
        }
        finally
        {
            _opening--;
        }
    }

    // Ticker: drop corpses and replenish toward PoolSize.
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

        if (!_disposed && _connections.Count + _opening < _options.PoolSize)
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

        foreach (Http2ClientConnection connection in _connections)
        {
            connection.Dispose();
        }
        _connections.Clear();
    }
}
