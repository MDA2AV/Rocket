namespace ioxide.pg;

/// <summary>
/// N Postgres connections on one reactor's ring. Handlers rent, query, return; when all are busy,
/// renters queue and resume inline as connections come back. Warmup opens connections
/// concurrently over the ring; broken ones are discarded on return and replaced in the
/// background. One pool per reactor - create from <c>Reactor.OnStart</c>.
/// </summary>
public sealed class PgPool
{
    private readonly RingPool<PgConnection> _pool = new();
    private readonly IRingHost _host;
    private readonly PgOptions _options;

    private PgPool(IRingHost host, PgOptions options)
    {
        _host = host;
        _options = options;
    }

    /// <summary>Open <see cref="PgOptions.PoolSize"/> connections and register the pool as a service.</summary>
    public static PgPool Start(Reactor reactor, PgOptions options)
    {
        var pool = new PgPool(reactor, options);

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
            PgConnection connection = await PgConnection.ConnectAsync(_host, _options);
            _pool.Return(connection);
        }
        catch (Exception e)
        {
            // The pool runs one connection short; queries queue on the remaining ones.
            Console.Error.WriteLine($"[pg] connect to {_options.Host}:{_options.Port} failed: {e.Message}");
        }
    }

    /// <summary>Connections currently idle (for diagnostics).</summary>
    public int IdleCount => _pool.IdleCount;

    /// <summary>
    /// Rent a connection for several operations (e.g. a transaction). Pair with
    /// <see cref="Return"/> in a finally block.
    /// </summary>
    public ValueTask<PgConnection> RentAsync()
    {
        return _pool.RentAsync();
    }

    /// <summary>Return a rented connection; a broken one is replaced instead of pooled.</summary>
    public void Return(PgConnection connection)
    {
        if (connection.IsBroken)
        {
            connection.Dispose();
            _ = OpenOneAsync();
            return;
        }

        _pool.Return(connection);
    }

    /// <summary>Rent → stream a query's rows through <paramref name="onRow"/> → return.</summary>
    public async ValueTask<int> QueryRowsAsync(string sql, PgRowHandler onRow)
    {
        PgConnection connection = await RentAsync();
        try
        {
            return await connection.QueryRowsAsync(sql, onRow);
        }
        finally
        {
            Return(connection);
        }
    }

    /// <summary>Rent → run one simple query → return. The everyday path.</summary>
    public async ValueTask<PgResult> QueryAsync(string sql)
    {
        PgConnection connection = await RentAsync();
        try
        {
            return await connection.QueryAsync(sql);
        }
        finally
        {
            Return(connection);
        }
    }
}
