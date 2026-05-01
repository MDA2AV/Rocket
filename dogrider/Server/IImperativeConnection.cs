using dogrider.Protocol;

namespace dogrider.Server;

/// <summary>
/// One WebSocket connection. All read calls must come from the same task (single-reader);
/// sends serialize via the underlying zerg connection's write buffer (call FlushAsync after
/// queuing one or more sends).
/// </summary>
public interface IImperativeConnection : IAsyncDisposable
{
    /// <summary>
    /// Reads the next frame. The returned <see cref="WebsocketFrame.Payload"/> is valid only
    /// until the next call to this method.
    /// </summary>
    ValueTask<WebsocketFrame> ReadFrameAsync(CancellationToken cancellationToken = default);

    /// <summary>Buffer a Text frame (UTF-8) into the write buffer. Call FlushAsync to send.</summary>
    void WriteText(ReadOnlySpan<byte> utf8Payload, bool fin = true);

    /// <summary>Buffer a Text frame from a string. Call FlushAsync to send.</summary>
    void WriteText(string text, bool fin = true);

    /// <summary>Buffer a Binary frame. Call FlushAsync to send.</summary>
    void WriteBinary(ReadOnlySpan<byte> payload, bool fin = true);

    /// <summary>Buffer a Pong with optional payload (echo what came in the Ping).</summary>
    void WritePong(ReadOnlySpan<byte> payload);

    /// <summary>Buffer a Ping (zero or small app payload).</summary>
    void WritePing(ReadOnlySpan<byte> payload = default);

    /// <summary>Buffer a Close frame with status code and optional reason.</summary>
    void WriteClose(ushort statusCode = 1000, string? reason = null);

    /// <summary>Flush any buffered frames out via zerg's send path.</summary>
    ValueTask FlushAsync();
}
