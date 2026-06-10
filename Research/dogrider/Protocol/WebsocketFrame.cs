using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace dogrider.Protocol;

/// <summary>
/// A single decoded WebSocket frame. The <see cref="Payload"/> sequence may point directly into
/// kernel-provided io_uring buffers and is therefore only valid until the next call to
/// <c>ReadFrameAsync</c> on the owning connection. If the caller needs to retain the bytes,
/// copy via <c>Payload.ToArray()</c> or the convenience <see cref="DataCopy"/> property.
/// </summary>
public readonly struct WebsocketFrame
{
    public FrameType Type { get; }
    public bool Fin { get; }
    public ReadOnlySequence<byte> Payload { get; }

    private readonly FrameError? _error;

    /// <summary>
    /// Convenience copy. Allocates a managed array. Use <see cref="Payload"/> for zero-copy access.
    /// </summary>
    public ReadOnlyMemory<byte> DataCopy => Payload.IsEmpty ? ReadOnlyMemory<byte>.Empty : Payload.ToArray();

    public WebsocketFrame(FrameType type, ReadOnlySequence<byte> payload, bool fin)
    {
        Type = type;
        Fin = fin;
        Payload = payload;
        _error = null;
    }

    public WebsocketFrame(FrameError error)
    {
        Type = FrameType.Error;
        Fin = true;
        Payload = ReadOnlySequence<byte>.Empty;
        _error = error;
    }

    public bool IsError([MaybeNullWhen(false)] out FrameError error)
    {
        error = _error;
        return _error != null;
    }
}
