namespace ioxide;

// This part is intentionally NOT unsafe: it awaits the connection handler, and await isn't allowed
// in an unsafe context (mirror of the TCP handler part).
public sealed partial class Reactor
{
    // Invoke the per-connection QUIC handler, observing faults so a thrown handler doesn't silently
    // vanish (the transport starts it fire-and-forget from the adopt path). Runs once per connection.
    private async Task RunQuicHandlerAsync(QuicConnection conn)
    {
        int gen = conn.Generation;
        try
        {
            await QuicHandle!(this, conn);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[r{_id}] quic handler faulted: {e.GetBaseException().Message}");

            // A handler that faulted before its DecRef would otherwise leak the handler ref.
            conn.ReleaseHandlerRefOnFault(gen);
        }
    }
}
