using System.Runtime.CompilerServices;
using RingPg;
using static Minima.Native;

namespace Minima;

/// <summary>
/// Minima as an <see cref="IRingHost"/>: a RingPg client (Postgres) submits SEND/RECV on its DB
/// socket through this reactor's ring, tagged <c>KindDb</c>; the matching CQE is routed back to
/// the client's IVTS in Dispatch — all on the reactor thread, no .NET socket engine, no pool.
/// One DB connection per reactor (queries are serial on it).
/// </summary>
public sealed unsafe partial class Reactor : IRingHost
{
    private const ulong KindDb = 5UL << 32;
    private IRingCompletion? _dbTarget;

    /// This reactor's Postgres connection (opened in Run when MINIMA_DB=1). Null otherwise.
    public PgConnection? Db;

    public void Bind(int fd, IRingCompletion target) => _dbTarget = target;

    public void SubmitSend(int fd, nint buf, int len)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_SEND;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = (uint)len;
        sqe->user_data = KindDb | (uint)fd;
    }

    public void SubmitRecv(int fd, nint buf, int len)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECV;       // plain recv into the client's buffer (no buf-select)
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = (uint)len;
        sqe->user_data = KindDb | (uint)fd;
    }
}
