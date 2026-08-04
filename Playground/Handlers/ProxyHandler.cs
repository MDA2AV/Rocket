using System.Text;
using ioxide;
using ioxide.http11;
using Playground.Http;

namespace Playground.Handlers;

/// <summary>
/// proxy - every inbound request is forwarded to an upstream origin through the ring-native HTTP
/// client, and the upstream's status and body are relayed back. Both hops - the inbound connection
/// and the outbound call - run on this reactor's ring and resume inline, so a proxied request never
/// leaves the thread it arrived on.
/// </summary>
internal static class ProxyHandler
{
    public static Task Handle(Reactor reactor, TcpConnection conn)
        => ConnectionLoop.ServeAsync(conn, new ProxyResponder(reactor.GetService<HttpClientPool>()));

    private readonly struct ProxyResponder(HttpClientPool upstream) : ITcpResponder
    {
        public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
        {
            string path = RequestParser.ReadPath(conn, snapshot);

            try
            {
                using HttpClientResponse response = await upstream.GetAsync(path);
                conn.Write(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {response.Status} X\r\nContent-Length: {response.Body.Length}\r\n\r\n"));
                conn.Write(response.Body.Span);
            }
            catch (HttpClientException e)
            {
                byte[] message = Encoding.ASCII.GetBytes($"upstream: {e.Message}");
                conn.Write(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 502 Bad Gateway\r\nContent-Length: {message.Length}\r\n\r\n"));
                conn.Write(message);
            }

            await conn.FlushAsync();
        }
    }
}
