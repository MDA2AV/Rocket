namespace ioxide.redis;

/// <summary>
/// N Redis connections on one reactor's ring, with pipelining. Commands round-robin across the
/// connections and each connection multiplexes many in flight, so the connections stay busy under
/// load instead of one round trip at a time. Broken connections are evicted and replaced. One pool
/// per reactor - create from <c>Reactor.OnStart</c>.
/// </summary>
public sealed class RedisPool
{
    private readonly List<RedisConnection> _connections = new();
    private readonly IRingHost _host;
    private readonly RedisOptions _options;
    private int _next;
    private TaskCompletionSource<bool>? _opened;

    private RedisPool(IRingHost host, RedisOptions options)
    {
        _host = host;
        _options = options;
    }

    /// <summary>Open <see cref="RedisOptions.PoolSize"/> connections and register the pool as a service.</summary>
    public static RedisPool Start(Reactor reactor, RedisOptions options)
    {
        var pool = new RedisPool(reactor, options);
        for (int i = 0; i < options.PoolSize; i++)
        {
            _ = pool.OpenOneAsync();
        }
        reactor.AddService(pool);
        return pool;
    }

    private async Task OpenOneAsync()
    {
        try
        {
            RedisConnection connection = await RedisConnection.ConnectAsync(_host, _options);
            _connections.Add(connection);
            _opened?.TrySetResult(true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[redis] connect to {_options.Host}:{_options.Port} failed: {e.Message}");
        }
    }

    /// <summary>Live connections (for diagnostics).</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>Run any command on the next connection (pipelined).</summary>
    public ValueTask<RespValue> ExecuteAsync(string command, params RedisArg[] args)
    {
        RedisConnection? c = Pick();
        return c != null ? c.ExecuteAsync(command, args) : ExecuteSlowAsync(command, args);
    }

    /// <summary>GET (the cache-aside read path).</summary>
    public ValueTask<string?> GetAsync(string key)
    {
        RedisConnection? c = Pick();
        return c != null ? c.GetAsync(key) : GetSlowAsync(key);
    }

    /// <summary>SET key value EX seconds (the cache-aside populate path).</summary>
    public ValueTask SetExAsync(string key, RedisArg value, int seconds)
    {
        RedisConnection? c = Pick();
        return c != null ? c.SetExAsync(key, value, seconds) : SetExSlowAsync(key, value, seconds);
    }

    /// <summary>DEL (cache invalidation).</summary>
    public ValueTask<long> DelAsync(params string[] keys)
    {
        RedisConnection? c = Pick();
        return c != null ? c.DelAsync(keys) : DelSlowAsync(keys);
    }

    private async ValueTask<RespValue> ExecuteSlowAsync(string command, RedisArg[] args)
        => await (await WaitForConnectionAsync()).ExecuteAsync(command, args);
    private async ValueTask<string?> GetSlowAsync(string key)
        => await (await WaitForConnectionAsync()).GetAsync(key);
    private async ValueTask SetExSlowAsync(string key, RedisArg value, int seconds)
        => await (await WaitForConnectionAsync()).SetExAsync(key, value, seconds);
    private async ValueTask<long> DelSlowAsync(string[] keys)
        => await (await WaitForConnectionAsync()).DelAsync(keys);

    // Round-robin over healthy connections; evict and replace any found broken.
    private RedisConnection? Pick()
    {
        while (_connections.Count > 0)
        {
            int index = (_next++ & 0x7fffffff) % _connections.Count;
            RedisConnection c = _connections[index];
            if (!c.IsBroken)
            {
                return c;
            }
            _connections.RemoveAt(index);
            c.Dispose();
            _ = OpenOneAsync();
        }
        return null;
    }

    private async ValueTask<RedisConnection> WaitForConnectionAsync()
    {
        while (true)
        {
            RedisConnection? c = Pick();
            if (c != null)
            {
                return c;
            }
            _opened ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _opened.Task;
            _opened = null;
        }
    }
}
