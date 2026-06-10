using System.Buffers.Text;
using System.Text.Json;

namespace Loom;

/// <summary>
/// Demo handler. Two paths show the point:
///   GET /baseline11?a=&b=  → a+b, fully synchronous — the handler never leaves the reactor.
///   GET /work              → `await Task.Run(...)` (off-reactor) — the continuation is woven
///                             back to the reactor by the SyncContext; the response reports the
///                             thread so you can SEE it resumed on the reactor before sending.
///
/// (Deliberately minimal: GET, one complete request per recv, keep-alive — enough to prove the
/// model and benchmark it, not the full HTTP contract.)
/// </summary>
internal static class Http
{
    /// LOOM_MINIMA=1 → respond a fixed "ok" per recv (Handle2), identical bytes to Minima,
    /// for an apples-to-apples engine bench.
    internal static bool MinimaMode;

    /// LOOM_WORK=1 → per request, await off-reactor Task.Run work (Minima's async-work knob).
    internal static bool Work;

    /// LOOM_RING=1 → per request, await an io_uring NOP. Async, but the op lives on the ring,
    /// so it completes on the reactor and resumes inline — NO thread pool.
    internal static bool RingWork;

    /// LOOM_DELAY_US=&lt;n&gt; → with LOOM_RING=1, await an io_uring TIMEOUT of n microseconds
    /// instead of a NOP — real async latency, still no thread pool.
    internal static long DelayUs;

    /// LOOM_DB=1 → each reactor opens a Postgres connection whose SEND/RECV ride the ring;
    /// GET /db runs "SELECT 42" over it (await resumes inline — no thread pool).
    internal static bool UseDb;

    /// <summary>
    /// Minima-equivalent handler: does NOT parse the request — writes the exact same fixed
    /// "ok" response Minima emits, once per recv. Synchronous when <see cref="Work"/> is off
    /// (no async machinery); when on, it awaits a thread-pool Task per request and the
    /// continuation is woven back to the reactor (SyncContext) before the SEND.
    /// </summary>
    internal static unsafe Task Handle2(Reactor reactor, Connection conn)
    {
        conn.RecvLen = 0;   // consume whatever arrived (Minima doesn't parse either)
        if (RingWork) return Handle2Ring(reactor, conn);
        if (Work) return Handle2Work(reactor, conn);

        WriteOk(conn);
        reactor.Send(conn);
        return Task.CompletedTask;
    }

    /// io_uring-native async: the await is backed by a ring CQE (a NOP), so it completes ON the
    /// reactor and resumes inline — NO thread pool, unlike Handle2Work's Task.Run.
    private static async Task Handle2Ring(Reactor reactor, Connection conn)
    {
        if (DelayUs > 0) await reactor.DelayAsync(conn, DelayUs);   // real async latency, no pool
        else await reactor.RingYieldAsync(conn);                    // instant NOP
        WriteOk(conn);
        reactor.Send(conn);
    }

    /// Async-work variant: the Task.Run runs off-reactor on the thread pool; its continuation
    /// (WriteOk + Send) resumes ON THE REACTOR via the LoomSyncContext, so the SEND is a legal
    /// ring submit. Touches no pointers directly, so it can be `async`.
    private static async Task Handle2Work(Reactor reactor, Connection conn)
    {
        _ = await Task.Run(static () => JsonSerializer.Serialize("Hello World!"));
        
        WriteOk(conn);          // back on the reactor thread
        reactor.Send(conn);
    }

    private static unsafe void WriteOk(Connection conn)
    {
        ReadOnlySpan<byte> resp =
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8;
        resp.CopyTo(new Span<byte>(conn.Write, Connection.WriteBuf));
        conn.WriteLen = resp.Length;
    }

    /// Synchronous entry: parse (unsafe spans) and either respond inline (baseline) or hand
    /// off to the async work path. Returns a Task the reactor ignores.
    internal static unsafe Task Handle(Reactor reactor, Connection conn)
    {
        if (MinimaMode) return Handle2(reactor, conn);

        var buf = new ReadOnlySpan<byte>(conn.Recv, conn.RecvLen);
        int he = buf.IndexOf("\r\n\r\n"u8);
        if (he < 0) { reactor.ArmRecv(conn); return Task.CompletedTask; }   // incomplete

        ReadOnlySpan<byte> target = default;
        int sp1 = buf.IndexOf((byte)' ');
        if (sp1 >= 0)
        {
            ReadOnlySpan<byte> rest = buf[(sp1 + 1)..];
            int sp2 = rest.IndexOf((byte)' ');
            target = sp2 >= 0 ? rest[..sp2] : rest;
        }
        int q = target.IndexOf((byte)'?');
        ReadOnlySpan<byte> path = q >= 0 ? target[..q] : target;
        ReadOnlySpan<byte> query = q >= 0 ? target[(q + 1)..] : default;

        conn.RecvLen = 0;   // consumed (demo: one request per recv)

        if (path.SequenceEqual("/work"u8))
            return HandleWork(reactor, conn);   // async path — no spans cross the await
        if (path.SequenceEqual("/db"u8))
            return HandleDb(reactor, conn);     // io_uring Postgres query

        WriteText(conn, SumAB(query));          // baseline11 — synchronous, never yields
        reactor.Send(conn);
        return Task.CompletedTask;
    }

    /// The async path: do off-reactor work, then resume HERE (woven back by the SyncContext)
    /// and respond. Note: this method touches no pointers directly — it calls unsafe helpers,
    /// so it can be `async`.
    private static async Task HandleWork(Reactor reactor, Connection conn)
    {
        int beforeThread = Environment.CurrentManagedThreadId;

        long sum = await Task.Run(static () =>
        {
            long s = 0;
            for (int i = 1; i <= 1000; i++) s += i;
            return s;
        });

        bool onReactor = reactor.OnReactorThread;   // true ⇒ the SyncContext brought us home
        WriteWork(conn, sum, beforeThread, onReactor);
        reactor.Send(conn);                          // submit on the reactor thread
    }

    /// io_uring-native Postgres: the query SEND and response RECV ride the reactor's ring, so
    /// `await reactor.DbQueryAsync(...)` resumes inline — no .NET socket engine, no thread pool.
    private static async Task HandleDb(Reactor reactor, Connection conn)
    {
        string v = await DbQuery(reactor, "SELECT 42");
        WriteDb(conn, v);
        reactor.Send(conn);
    }

    // The async orchestration (lives here, not in the `unsafe` Reactor): SEND the query, then
    // RECV until ReadyForQuery — both ride the ring, both resume inline on the reactor.
    private static async Task<string> DbQuery(Reactor reactor, string sql)
    {
        int len = reactor.DbPrepareQuery(sql);
        await reactor.DbSendAsync(len);
        reactor.DbResetRecv();
        while (true)
        {
            int n = await reactor.DbRecvAsync();
            if (n <= 0) return "";
            if (reactor.DbFinishRecv(n, out string result)) return result;
        }
    }

    private static unsafe void WriteDb(Connection conn, string v)
    {
        Span<byte> body = stackalloc byte[64];
        int p = 0;
        Append(body, ref p, "db="u8);
        p += System.Text.Encoding.ASCII.GetBytes(v, body[p..]);
        Emit(conn, body[..p]);
    }

    private static unsafe void WriteText(Connection conn, long value)
    {
        Span<byte> body = stackalloc byte[24];
        Utf8Formatter.TryFormat(value, body, out int n);
        Emit(conn, body[..n]);
    }

    private static unsafe void WriteWork(Connection conn, long sum, int beforeThread, bool onReactor)
    {
        Span<byte> body = stackalloc byte[96];
        int p = 0;
        Append(body, ref p, "work="u8); AppendLong(body, ref p, sum);
        Append(body, ref p, " before-thread="u8); AppendLong(body, ref p, beforeThread);
        Append(body, ref p, " resumed-on-reactor="u8); Append(body, ref p, onReactor ? "true"u8 : "false"u8);
        Emit(conn, body[..p]);
    }

    private static unsafe void Emit(Connection conn, ReadOnlySpan<byte> body)
    {
        var w = new Span<byte>(conn.Write, Connection.WriteBuf);
        int pos = 0;
        Append(w, ref pos, "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: "u8);
        AppendLong(w, ref pos, body.Length);
        Append(w, ref pos, "\r\n\r\n"u8);
        body.CopyTo(w[pos..]); pos += body.Length;
        conn.WriteLen = pos;
    }

    private static void Append(Span<byte> w, ref int pos, ReadOnlySpan<byte> src) { src.CopyTo(w[pos..]); pos += src.Length; }
    private static void AppendLong(Span<byte> w, ref int pos, long v) { Utf8Formatter.TryFormat(v, w[pos..], out int n); pos += n; }

    private static long SumAB(ReadOnlySpan<byte> query)
    {
        long a = 0, b = 0;
        while (query.Length > 0)
        {
            int amp = query.IndexOf((byte)'&');
            ReadOnlySpan<byte> kv = amp >= 0 ? query[..amp] : query;
            int eq = kv.IndexOf((byte)'=');
            if (eq >= 0)
            {
                ReadOnlySpan<byte> k = kv[..eq];
                if (k.SequenceEqual("a"u8)) Utf8Parser.TryParse(kv[(eq + 1)..], out a, out _);
                else if (k.SequenceEqual("b"u8)) Utf8Parser.TryParse(kv[(eq + 1)..], out b, out _);
            }
            if (amp < 0) break;
            query = query[(amp + 1)..];
        }
        return a + b;
    }
}
