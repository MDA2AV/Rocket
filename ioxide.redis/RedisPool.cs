namespace ioxide.redis;

/// <summary>
/// N Redis connections on one reactor's ring. Handlers rent, run commands, return; when all are
/// busy, renters queue and resume inline as connections come back. Broken connections are replaced
/// on return. One pool per reactor - create from <c>Reactor.OnStart</c>.
/// </summary>
public sealed class RedisPool
{
    private readonly RingPool<RedisConnection> _pool = new();
    private readonly IRingHost _host;
    private readonly RedisOptions _options;

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
            _pool.Return(connection);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[redis] connect to {_options.Host}:{_options.Port} failed: {e.Message}");
        }
    }

    public int IdleCount => _pool.IdleCount;

    /// <summary>Rent a connection for several commands (e.g. MULTI/EXEC or a pipeline); return in a finally.</summary>
    public ValueTask<RedisConnection> RentAsync() => _pool.RentAsync();

    public void Return(RedisConnection connection)
    {
        if (connection.IsBroken)
        {
            connection.Dispose();
            _ = OpenOneAsync();
            return;
        }
        _pool.Return(connection);
    }

    /// <summary>Rent → run one command → return.</summary>
    public async ValueTask<RespValue> ExecuteAsync(string command, params RedisArg[] args)
    {
        RedisConnection connection = await RentAsync();
        try
        {
            return await connection.ExecuteAsync(command, args);
        }
        finally
        {
            Return(connection);
        }
    }

    /// <summary>Rent → GET → return (the cache-aside read path).</summary>
    public async ValueTask<string?> GetAsync(string key)
    {
        RedisConnection connection = await RentAsync();
        try
        {
            return await connection.GetAsync(key);
        }
        finally
        {
            Return(connection);
        }
    }

    /// <summary>Rent → SET key value EX seconds → return (the cache-aside populate path).</summary>
    public async ValueTask SetExAsync(string key, RedisArg value, int seconds)
    {
        RedisConnection connection = await RentAsync();
        try
        {
            await connection.SetExAsync(key, value, seconds);
        }
        finally
        {
            Return(connection);
        }
    }

    /// <summary>Rent → DEL → return (cache invalidation).</summary>
    public async ValueTask<long> DelAsync(params string[] keys)
    {
        RedisConnection connection = await RentAsync();
        try
        {
            return await connection.DelAsync(keys);
        }
        finally
        {
            Return(connection);
        }
    }
}
