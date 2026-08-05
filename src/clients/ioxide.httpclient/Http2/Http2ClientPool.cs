using ioxide.httpclient;

namespace ioxide.httpclient;

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

    // Callers that arrived before a connection was up. Without these the pool fails a request the
    // instant it is created - the connect is asynchronous, so the first requests would all throw
    // and a caller that retries would spin.
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();

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
        return connection is not null
            ? SendWithRetryAsync(connection, request)
            : SendWhenConnectedAsync(request);
    }

    // A refused stream was never processed by the peer, so resending it is safe - and expected,
    // since it is how a server retires a connection (GOAWAY, or a max-requests limit). Picking
    // again lands on a replacement, because the refused connection has marked itself broken.
    private async ValueTask<HttpClientResponse> SendWithRetryAsync(
        Http2ClientConnection connection, HttpClientRequest request)
    {
        try
        {
            return await connection.SendAsync(request);
        }
        catch (Http2StreamRefusedException)
        {
            return await SendWhenConnectedAsync(request);
        }
    }

    private async ValueTask<HttpClientResponse> SendWhenConnectedAsync(HttpClientRequest request)
    {
        long deadline = Environment.TickCount64 + _options.AcquireTimeoutMs;

        while (true)
        {
            if (_disposed)
            {
                throw new Http2ClientException("pool disposed");
            }

            // Pick prunes broken connections as a side effect, so it must run BEFORE the
            // replenish check below - a retiring corpse still parked in _connections would
            // otherwise count as alive, nobody would open a replacement, and every waiter
            // would stall until the sweep ticker got around to it.
            Http2ClientConnection? connection = Pick();
            if (connection is not null)
            {
                try
                {
                    return await connection.SendAsync(request);
                }
                catch (Http2StreamRefusedException)
                {
                    continue;   // that connection is retiring too; try the replacement
                }
            }

            int remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0)
            {
                throw new Http2ClientException(
                    $"no HTTP/2 connection to {_options.Host}:{_options.Port} within {_options.AcquireTimeoutMs} ms");
            }

            if (_connections.Count + _opening == 0)
            {
                OpenOne();   // every connection died and the sweep has not run yet
            }

            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);

            Task signalled = waiter.Task;
            if (await Task.WhenAny(signalled, Task.Delay(remaining)) != signalled)
            {
                waiter.TrySetCanceled();   // abandon the slot so WakeWaiters cannot hand it to us
            }
        }
    }

    private void WakeWaiters()
    {
        while (_waiters.Count > 0)
        {
            _waiters.Dequeue().TrySetResult(true);
        }
    }

    private Http2ClientConnection? Pick()
    {
        Prune();

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
            WakeWaiters();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[nghttp2] connect to {_options.Host}:{_options.Port} failed: {e.Message}");
            WakeWaiters();   // fail fast: waiters re-check and time out rather than hang
        }
        finally
        {
            _opening--;
        }
    }

    // Remove broken connections from rotation. A retiring connection that still has requests in
    // flight is only REMOVED, never disposed - nginx keeps answering streams at or below the
    // GOAWAY's last_stream_id, and disposing here would close the socket under those responses.
    // Its own pump disposes it once it drains. Hard-failed connections have already failed their
    // pending requests, so InFlight is 0 and they are disposed on the spot.
    private void Prune()
    {
        for (int i = _connections.Count - 1; i >= 0; i--)
        {
            Http2ClientConnection connection = _connections[i];
            if (connection.IsBroken)
            {
                if (connection.InFlight == 0)
                {
                    connection.Dispose();
                }
                _connections.RemoveAt(i);
            }
        }
    }

    // Ticker: drop corpses and replenish toward PoolSize.
    private void Sweep()
    {
        Prune();

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
        WakeWaiters();
    }
}
