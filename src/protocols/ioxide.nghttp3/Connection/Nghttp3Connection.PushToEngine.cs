namespace ioxide.nghttp3;

/// <summary>Ingress: one recv-queue item at a time into nghttp3 (bytes via ih3_read_stream, QUIC
/// stream lifecycle mirrored into shutdown/close calls). nghttp3's callbacks fire synchronously
/// inside these calls and deposit into the per-stream assembly state.</summary>
public sealed partial class Nghttp3Connection
{
    private unsafe void PushToEngine(in QuicRecvRing.Delivery item)
    {
        // Lifecycle items: mirror the QUIC stream state into nghttp3 so a cancelled request is
        // torn down on both sides instead of half-ignored (a dangling stream keeps per-stream
        // state alive and can feed the peer a confusing half-open response).
        if (item.Kind != QuicStreamEvent.Data)
        {
            if (_requests.Remove(item.StreamId, out Nghttp3Request? gone))
            {
                if (!gone.Dispatched || gone.HandlerDone)
                {
                    ReleaseRequest(gone);   // never exposed, or the handler already finished
                }
                else
                {
                    gone.Detached = true;   // streaming handler still running - it recycles on completion
                }
            }
            if (_sinks.Remove(item.StreamId, out Nghttp3BodyReader? deadSink))
            {
                deadSink.End();   // parked body reads resume empty on this pass's FireBodyWakes
            }

            int eventResult = item.Kind switch
            {
                QuicStreamEvent.Reset       => Nghttp3.ih3_shutdown_stream_read(_nghttp3Handle, item.StreamId),
                QuicStreamEvent.StopSending => Nghttp3.ih3_shutdown_stream_write(_nghttp3Handle, item.StreamId),
                _                                  => Nghttp3.ih3_close_stream(_nghttp3Handle, item.StreamId, item.AppError),
            };
            if (eventResult < 0)
            {
                Console.Error.WriteLine($"[ioxide.nghttp3] stream {item.Kind} handling failed: {Nghttp3.StrError(eventResult)}");
                _protocolFailed = true;
            }

            return;
        }

        long readResult;
        ReadOnlySpan<byte> payload = item.AsSpan();   // covers arena-backed and pooled items alike
        fixed (byte* payloadPointer = payload)
        {
            readResult = Nghttp3.ih3_read_stream(_nghttp3Handle, item.StreamId, payloadPointer, (nuint)payload.Length, item.Fin ? 1 : 0);
        }

        if (readResult < 0)
        {
            Console.Error.WriteLine($"[ioxide.nghttp3] read_stream failed: {Nghttp3.StrError((int)readResult)}");
            _protocolFailed = true;

            return;
        }

        // Paced stream: readResult is nghttp3's creditable share of these bytes (framing/QPACK overhead,
        // excluding DATA payload - that credits as the handler consumes it from the sink).
        if (readResult > 0 && _sinks.ContainsKey(item.StreamId))
        {
            _quicConnection.ConsumeStreamData(item.StreamId, readResult);
        }
    }
}
