namespace ioxide.redis;

/// <summary>
/// Typed convenience commands over <see cref="ExecuteAsync(string, RedisArg[])"/>. Not exhaustive -
/// anything missing runs through ExecuteAsync directly - but it covers the everyday surface of each
/// data type.
/// </summary>
public sealed partial class RedisConnection
{
    // -- strings ----------------------------------------------------------

    public async ValueTask<string?> GetAsync(string key) =>
        (await ExecuteAsync("GET", key)).AsString();

    public async ValueTask<byte[]?> GetBytesAsync(string key) =>
        (await ExecuteAsync("GET", key)).Bytes;

    public async ValueTask SetAsync(string key, RedisArg value) =>
        await ExecuteAsync("SET", key, value);

    /// <summary>SET with an expiry in seconds (the cache-aside populate path).</summary>
    public async ValueTask SetExAsync(string key, RedisArg value, int seconds) =>
        await ExecuteAsync("SET", key, value, "EX", seconds);

    /// <summary>SET only if absent; returns true if the key was set.</summary>
    public async ValueTask<bool> SetNxAsync(string key, RedisArg value) =>
        !(await ExecuteAsync("SET", key, value, "NX")).IsNull;

    public async ValueTask<string?> GetSetAsync(string key, RedisArg value) =>
        (await ExecuteAsync("GETSET", key, value)).AsString();

    public async ValueTask<string?> GetDelAsync(string key) =>
        (await ExecuteAsync("GETDEL", key)).AsString();

    public async ValueTask<long> IncrAsync(string key) => (await ExecuteAsync("INCR", key)).AsInteger();
    public async ValueTask<long> DecrAsync(string key) => (await ExecuteAsync("DECR", key)).AsInteger();
    public async ValueTask<long> IncrByAsync(string key, long by) => (await ExecuteAsync("INCRBY", key, by)).AsInteger();
    public async ValueTask<long> AppendAsync(string key, RedisArg value) => (await ExecuteAsync("APPEND", key, value)).AsInteger();
    public async ValueTask<long> StrLenAsync(string key) => (await ExecuteAsync("STRLEN", key)).AsInteger();

    public async ValueTask<string?[]> MGetAsync(params string[] keys)
    {
        var args = Array.ConvertAll(keys, k => (RedisArg)k);
        RespValue reply = await ExecuteAsync("MGET", args);
        return Array.ConvertAll(reply.Items, v => v.AsString());
    }

    public async ValueTask MSetAsync(params (string Key, RedisArg Value)[] pairs)
    {
        var args = new RedisArg[pairs.Length * 2];
        for (int i = 0; i < pairs.Length; i++)
        {
            args[i * 2] = pairs[i].Key;
            args[i * 2 + 1] = pairs[i].Value;
        }
        await ExecuteAsync("MSET", args);
    }

    // -- keys -------------------------------------------------------------

    public async ValueTask<long> DelAsync(params string[] keys) =>
        (await ExecuteAsync("DEL", Array.ConvertAll(keys, k => (RedisArg)k))).AsInteger();

    public async ValueTask<long> UnlinkAsync(params string[] keys) =>
        (await ExecuteAsync("UNLINK", Array.ConvertAll(keys, k => (RedisArg)k))).AsInteger();

    public async ValueTask<bool> ExistsAsync(string key) => (await ExecuteAsync("EXISTS", key)).AsInteger() > 0;
    public async ValueTask<bool> ExpireAsync(string key, int seconds) => (await ExecuteAsync("EXPIRE", key, seconds)).AsBool();
    public async ValueTask<bool> PExpireAsync(string key, long ms) => (await ExecuteAsync("PEXPIRE", key, ms)).AsBool();
    public async ValueTask<bool> PersistAsync(string key) => (await ExecuteAsync("PERSIST", key)).AsBool();
    public async ValueTask<long> TtlAsync(string key) => (await ExecuteAsync("TTL", key)).AsInteger();
    public async ValueTask<string?> TypeAsync(string key) => (await ExecuteAsync("TYPE", key)).AsString();
    public async ValueTask RenameAsync(string key, string newKey) => await ExecuteAsync("RENAME", key, newKey);

    // -- hashes -----------------------------------------------------------

    public async ValueTask<string?> HGetAsync(string key, string field) => (await ExecuteAsync("HGET", key, field)).AsString();
    public async ValueTask<long> HSetAsync(string key, string field, RedisArg value) => (await ExecuteAsync("HSET", key, field, value)).AsInteger();
    public async ValueTask<bool> HExistsAsync(string key, string field) => (await ExecuteAsync("HEXISTS", key, field)).AsBool();
    public async ValueTask<long> HDelAsync(string key, params string[] fields)
    {
        var args = new RedisArg[fields.Length + 1];
        args[0] = key;
        for (int i = 0; i < fields.Length; i++) args[i + 1] = fields[i];
        return (await ExecuteAsync("HDEL", args)).AsInteger();
    }
    public async ValueTask<long> HLenAsync(string key) => (await ExecuteAsync("HLEN", key)).AsInteger();
    public async ValueTask<long> HIncrByAsync(string key, string field, long by) => (await ExecuteAsync("HINCRBY", key, field, by)).AsInteger();

    public async ValueTask<Dictionary<string, string>> HGetAllAsync(string key)
    {
        RespValue reply = await ExecuteAsync("HGETALL", key);
        var map = new Dictionary<string, string>(reply.Items.Length / 2);
        for (int i = 0; i + 1 < reply.Items.Length; i += 2)
        {
            map[reply.Items[i].AsString()!] = reply.Items[i + 1].AsString()!;
        }
        return map;
    }

    // -- lists ------------------------------------------------------------

    public async ValueTask<long> LPushAsync(string key, params RedisArg[] values) => (await ExecuteAsync("LPUSH", Prepend(key, values))).AsInteger();
    public async ValueTask<long> RPushAsync(string key, params RedisArg[] values) => (await ExecuteAsync("RPUSH", Prepend(key, values))).AsInteger();
    public async ValueTask<string?> LPopAsync(string key) => (await ExecuteAsync("LPOP", key)).AsString();
    public async ValueTask<string?> RPopAsync(string key) => (await ExecuteAsync("RPOP", key)).AsString();
    public async ValueTask<long> LLenAsync(string key) => (await ExecuteAsync("LLEN", key)).AsInteger();
    public async ValueTask<string?> LIndexAsync(string key, long index) => (await ExecuteAsync("LINDEX", key, index)).AsString();
    public async ValueTask LTrimAsync(string key, long start, long stop) => await ExecuteAsync("LTRIM", key, start, stop);

    public async ValueTask<string?[]> LRangeAsync(string key, long start, long stop)
    {
        RespValue reply = await ExecuteAsync("LRANGE", key, start, stop);
        return Array.ConvertAll(reply.Items, v => v.AsString());
    }

    // -- sets -------------------------------------------------------------

    public async ValueTask<long> SAddAsync(string key, params RedisArg[] members) => (await ExecuteAsync("SADD", Prepend(key, members))).AsInteger();
    public async ValueTask<long> SRemAsync(string key, params RedisArg[] members) => (await ExecuteAsync("SREM", Prepend(key, members))).AsInteger();
    public async ValueTask<bool> SIsMemberAsync(string key, RedisArg member) => (await ExecuteAsync("SISMEMBER", key, member)).AsBool();
    public async ValueTask<long> SCardAsync(string key) => (await ExecuteAsync("SCARD", key)).AsInteger();

    public async ValueTask<string?[]> SMembersAsync(string key)
    {
        RespValue reply = await ExecuteAsync("SMEMBERS", key);
        return Array.ConvertAll(reply.Items, v => v.AsString());
    }

    // -- sorted sets ------------------------------------------------------

    public async ValueTask<long> ZAddAsync(string key, double score, RedisArg member) => (await ExecuteAsync("ZADD", key, score, member)).AsInteger();
    public async ValueTask<long> ZRemAsync(string key, params RedisArg[] members) => (await ExecuteAsync("ZREM", Prepend(key, members))).AsInteger();
    public async ValueTask<long> ZCardAsync(string key) => (await ExecuteAsync("ZCARD", key)).AsInteger();
    public async ValueTask<double?> ZScoreAsync(string key, RedisArg member)
    {
        RespValue reply = await ExecuteAsync("ZSCORE", key, member);
        return reply.IsNull ? null : reply.AsDouble();
    }
    public async ValueTask<long?> ZRankAsync(string key, RedisArg member)
    {
        RespValue reply = await ExecuteAsync("ZRANK", key, member);
        return reply.IsNull ? null : reply.AsInteger();
    }
    public async ValueTask<double> ZIncrByAsync(string key, double by, RedisArg member) => (await ExecuteAsync("ZINCRBY", key, by, member)).AsDouble();

    public async ValueTask<string?[]> ZRangeAsync(string key, long start, long stop)
    {
        RespValue reply = await ExecuteAsync("ZRANGE", key, start, stop);
        return Array.ConvertAll(reply.Items, v => v.AsString());
    }

    // -- pub/sub & server -------------------------------------------------

    public async ValueTask<long> PublishAsync(string channel, RedisArg message) => (await ExecuteAsync("PUBLISH", channel, message)).AsInteger();
    public async ValueTask<string?> PingAsync() => (await ExecuteAsync("PING")).AsString();
    public async ValueTask<string?> EchoAsync(RedisArg message) => (await ExecuteAsync("ECHO", message)).AsString();
    public async ValueTask FlushDbAsync() => await ExecuteAsync("FLUSHDB");
    public async ValueTask<long> DbSizeAsync() => (await ExecuteAsync("DBSIZE")).AsInteger();

    // -- scripting --------------------------------------------------------

    public async ValueTask<RespValue> EvalAsync(string script, string[] keys, params RedisArg[] args)
    {
        var all = new RedisArg[2 + keys.Length + args.Length];
        all[0] = script;
        all[1] = keys.Length;
        for (int i = 0; i < keys.Length; i++) all[2 + i] = keys[i];
        for (int i = 0; i < args.Length; i++) all[2 + keys.Length + i] = args[i];
        return await ExecuteAsync("EVAL", all);
    }

    private static RedisArg[] Prepend(string key, RedisArg[] rest)
    {
        var args = new RedisArg[rest.Length + 1];
        args[0] = key;
        Array.Copy(rest, 0, args, 1, rest.Length);
        return args;
    }
}
