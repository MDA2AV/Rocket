namespace ioxide.pg;

/// <summary>
/// N Postgres connections on one reactor's ring, with pipelining. Commands round-robin across the
/// connections and each connection multiplexes many in flight, so the connections stay busy under
/// load instead of one round trip at a time. Broken connections are evicted and replaced. One pool
/// per reactor - create from <c>Reactor.OnStart</c>.
/// </summary>
public sealed class PgPool
{
    private readonly List<PgConnection> _connections = new();
    private readonly IRingHost _host;
    private readonly PgOptions _options;
    private int _next;
    private TaskCompletionSource<bool>? _opened;   // signalled when a connection becomes available

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
            _connections.Add(connection);
            _opened?.TrySetResult(true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[pg] connect to {_options.Host}:{_options.Port} failed: {e.Message}");
        }
    }

    /// <summary>Live connections (for diagnostics).</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>Run one simple query on the next connection (pipelined).</summary>
    public ValueTask<PgResult> QueryAsync(string sql)
    {
        PgConnection? c = Pick();
        return c != null ? c.QueryAsync(sql) : QuerySlowAsync(sql);
    }

    /// <summary>Run one simple query, streaming rows through <paramref name="onRow"/> (pipelined).</summary>
    public ValueTask<int> QueryRowsAsync(string sql, PgRowHandler onRow)
    {
        PgConnection? c = Pick();
        return c != null ? c.QueryRowsAsync(sql, onRow) : QueryRowsSlowAsync(sql, onRow);
    }

    /// <summary>Run a prepared parameterized query on the next connection (pipelined).</summary>
    public ValueTask<PgResult> QueryAsync(string sql, ReadOnlySpan<PgParam> args)
    {
        PgConnection? c = Pick();
        // Slow path is startup-only (no connection open yet); copying the params to the heap there is
        // fine, and keeps the span off the async state machine.
        return c != null ? c.QueryAsync(sql, args) : QuerySlowAsync(sql, args.ToArray());
    }

    /// <summary>Run a prepared parameterized query, streaming rows through <paramref name="onRow"/>. Read the count from the result.</summary>
    public ValueTask<PgResult> QueryAsync(string sql, ReadOnlySpan<PgParam> args, PgRowHandler onRow)
    {
        PgConnection? c = Pick();
        return c != null ? c.QueryAsync(sql, args, onRow) : QueryRowsSlowAsync(sql, args.ToArray(), onRow);
    }

    private async ValueTask<PgResult> QuerySlowAsync(string sql)
        => await (await WaitForConnectionAsync()).QueryAsync(sql);

    private async ValueTask<int> QueryRowsSlowAsync(string sql, PgRowHandler onRow)
        => await (await WaitForConnectionAsync()).QueryRowsAsync(sql, onRow);

    private async ValueTask<PgResult> QuerySlowAsync(string sql, PgParam[] args)
        => await (await WaitForConnectionAsync()).QueryAsync(sql, args);

    private async ValueTask<PgResult> QueryRowsSlowAsync(string sql, PgParam[] args, PgRowHandler onRow)
        => await (await WaitForConnectionAsync()).QueryAsync(sql, args, onRow);

    // Round-robin over healthy connections; evict and replace any found broken.
    private PgConnection? Pick()
    {
        while (_connections.Count > 0)
        {
            int index = (_next++ & 0x7fffffff) % _connections.Count;
            PgConnection c = _connections[index];
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

    // Startup-only: no connection is open yet; wait for the first one.
    private async ValueTask<PgConnection> WaitForConnectionAsync()
    {
        while (true)
        {
            PgConnection? c = Pick();
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
