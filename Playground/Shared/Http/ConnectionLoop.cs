using ioxide;

namespace Playground.Shared.Http;

/// <summary>
/// Produces the response for one read batch. Implementations are structs so the loop below can be
/// shared without adding indirection to the request path - see <see cref="ConnectionLoop"/>.
/// </summary>
public interface ITcpResponder
{
    /// <summary>
    /// Handle one batch of received bytes. The implementation owns draining the recv and flushing
    /// whatever it wrote (the file sample flushes several times per request, so the loop cannot do
    /// it on the responder's behalf).
    /// </summary>
    ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot);
}

/// <summary>
/// The connection loop every TCP sample shares: read, respond, repeat until the peer closes, then
/// drop the reference. Only the response differs between samples, which is what
/// <see cref="ITcpResponder"/> supplies.
/// </summary>
public static class ConnectionLoop
{
    /// <summary>
    /// Generic over a <c>struct</c> responder on purpose. The JIT compiles a separate instantiation
    /// per responder type and inlines <see cref="ITcpResponder.RespondAsync"/> into it, so sharing
    /// the loop costs no interface dispatch, no boxing and no closure allocation per request - the
    /// raw sample stays a clean throughput baseline.
    /// </summary>
    public static async Task ServeAsync<TResponder>(TcpConnection conn, TResponder responder)
        where TResponder : struct, ITcpResponder
    {
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                await responder.RespondAsync(conn, snapshot);

                if (snapshot.IsClosed) return;
                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    }
}
