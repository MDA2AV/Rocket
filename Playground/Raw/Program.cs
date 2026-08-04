using Playground.Shared;
using Playground.Shared.Http;

// raw - a fixed plaintext response, written straight to the connection. No I/O beyond the socket,
// so this is the throughput baseline every other sample is measured against.
//
//   PLAYGROUND_BODY=1024 dotnet run -c Release --project Playground/Raw

int bodyBytes = Responses.FixedBodyBytesFromEnvironment();
byte[] response = Responses.BuildFixedOk(bodyBytes);

return PlaygroundHost.Run(new PlaygroundSample
{
    Name = "raw",
    Summary = $"fixed {bodyBytes}-byte body, no I/O beyond the socket",
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(conn, new FixedResponder(response)),
});
