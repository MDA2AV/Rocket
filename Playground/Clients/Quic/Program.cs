using System.Diagnostics;
using ioxide;
using ioxide.ngtcp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  quic-client - the client half of QUIC on the ring, and the other side of Quic/Raw. Everything
//  else in Playground/Quic is a server; this is what CALLS one.
//
//  QuicClientEngine needs no listener of any kind: Connect asks the reactor for an outbound
//  transport on an ephemeral port, so Tcp, Udp and Quic all stay null in the config below. The
//  handshake, the stream and the reads all ride this reactor's ring and resume inline on it,
//  exactly like the server side.
//
//  Each connection opens ONE bidirectional stream and keeps ONE payload in flight - write, wait
//  for the echo, write again - so a "request" here is a round trip rather than a bandwidth
//  figure. That also makes it the load driver for the echo servers, which speak no HTTP and so
//  cannot be driven by wrk or h2load: bench/any.sh runs this against Quic/Raw, Quic/Pipe and
//  Quic/Alpn, and greps the "<n> req/s" line it prints.
//
//      dotnet run -c Release --project Playground/Quic/Raw          # something to talk to
//      dotnet run -c Release --project Playground/Clients/Quic
//
//  Needs: ioxide, ioxide.ngtcp2
// ─────────────────────────────────────────────────────────────────────────────────────────────

// ── Knobs ────────────────────────────────────────────────────────────────────────────────────
// Edit these. That is the whole mechanism - there is no config file and nothing else to find.
// Each Env.Override names the variable that can drive it instead, which is how the bench scripts
// run this sample; the literal is what applies otherwise.

string host        = "127.0.0.1";   // IPv4 literal - resolving a name would block the reactor
ushort port        = 8443;          // the echo server's UDP port
string serverName  = "localhost";   // SNI, and what the server certificate has to match
string alpn        = "echo";        // must match what the server offers
int    connections = 64;            // each opens one bidirectional stream
int    seconds     = 10;            // measured seconds, after a one-second warm-up
int    payloadSize = 64;            // bytes per round trip

Env.Override(ref host, "PLAYGROUND_ECHO_HOST");
Env.Override(ref port, "PLAYGROUND_QUIC_PORT");
Env.Override(ref serverName, "PLAYGROUND_ECHO_SNI");
Env.Override(ref alpn, "PLAYGROUND_ECHO_ALPN");
Env.Override(ref connections, "PLAYGROUND_ECHO_CONNS");
Env.Override(ref seconds, "PLAYGROUND_ECHO_SECONDS");
Env.Override(ref payloadSize, "PLAYGROUND_ECHO_BYTES");
// ─────────────────────────────────────────────────────────────────────────────────────────────

byte[] payload = new byte[payloadSize];
for (int i = 0; i < payload.Length; i++)
{
    payload[i] = (byte)('a' + i % 26);
}

long roundTrips = 0;
var  started    = new TaskCompletionSource();
var  deadline   = Stopwatch.StartNew();

// A client needs no listener of any kind: QuicClientEngine.Connect asks the reactor for an
// outbound transport on an ephemeral port, so Tcp, Udp and Quic all stay null here.
var config = new ServerConfig
{
    ReactorCount = 1,
    RingEntries  = 8192,
    Tcp          = null,
    Udp          = null,
    Quic         = null,
};

var engine  = new QuicClientEngine(alpn);
var reactor = new Reactor(0, config);

reactor.OnStart = r =>
{
    for (int i = 0; i < connections; i++)
    {
        QuicEngineConnection quic;
        try
        {
            quic = engine.Connect(r, host, port, serverName);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"connect failed: {e.Message}");
            started.TrySetResult();
            return;
        }

        // The stream cannot be opened before the handshake finishes, so the whole exchange hangs
        // off this callback rather than running straight after Connect.
        quic.HandshakeCompleted = () =>
        {
            started.TrySetResult();
            _ = EchoLoop(quic);
        };
    }
};

var thread = new Thread(reactor.Run) { Name = "quic-echo", IsBackground = true };
thread.Start();

// Give the handshakes a moment; if none completes there is nothing to measure and saying so beats
// reporting a zero that reads like a regression.
Task first = await Task.WhenAny(started.Task, Task.Delay(10_000));
if (first != started.Task)
{
    Console.Error.WriteLine($"no QUIC handshake completed against {host}:{port} (alpn {alpn})");
    return 1;
}

await Task.Delay(1_000);                       // warm: handshakes settle, first streams open
Interlocked.Exchange(ref roundTrips, 0);
deadline.Restart();
await Task.Delay(seconds * 1000);

long total = Interlocked.Read(ref roundTrips);
double elapsed = deadline.Elapsed.TotalSeconds;
Console.WriteLine($"{total / elapsed:F2} req/s   ({total} round trips in {elapsed:F1}s, "
                + $"{connections} connections, {payloadSize}-byte payload)");
return 0;

async Task EchoLoop(QuicEngineConnection quic)
{
    long streamId = quic.OpenBidiStream();
    if (streamId < 0)
    {
        return;
    }

    try
    {
        int outstanding = 0;
        quic.SendStream(streamId, payload, fin: false);

        while (deadline.Elapsed.TotalSeconds < seconds + 12)
        {
            QuicRecvSnapshot snapshot = await quic.ReadAsync();

            while (quic.TryGetDelivery(in snapshot, out QuicRecvRing.Delivery delivery))
            {
                outstanding += delivery.AsSpan().Length;
                quic.ReturnBuffer(in delivery);
            }

            // One payload back = one completed round trip. The echo can arrive split across
            // deliveries, so this counts bytes rather than deliveries.
            while (outstanding >= payload.Length)
            {
                outstanding -= payload.Length;
                Interlocked.Increment(ref roundTrips);
                quic.SendStream(streamId, payload, fin: false);
            }

            if (snapshot.IsClosed)
            {
                return;
            }
            quic.ResetRead();
        }
    }
    catch
    {
        // A connection dying mid-run is not fatal to the measurement; the others keep going and
        // the round-trip count reflects what actually completed.
    }
}
