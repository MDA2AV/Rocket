using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using ioxide;

// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace ioxide.h3;

/// <summary>
/// HTTP/3 over any <see cref="QuicConnection"/>: feeds the connection's stream items into nghttp3
/// and drains its egress back through SendStream. Use from a <c>Reactor.QuicHandle</c>:
///
/// <code>
/// reactor.QuicHandle = (r, conn) => new H3Connection(conn).RunAsync(
///     static req => H3Response.Text($"hello {req.Path}"));
/// </code>
///
/// One instance per connection; requests are assembled in full (headers + body), the handler runs
/// per request on the reactor thread, and the response is submitted and pumped out immediately.
/// The engine setup is lazy: the first wake happens on post-handshake bytes, which is when the
/// server's control/QPACK uni streams become openable.
/// </summary>
public sealed class H3Connection : IDisposable
{
    private readonly QuicConnection _quicConnection;
    private nint _h3;
    private GCHandle _self;
    private bool _fatal;

    private readonly Dictionary<long, H3Request> _requests = new();
    private readonly List<long> _ready = [];
    private readonly byte[] _egress = new byte[16 * 1024];

    public H3Connection(QuicConnection quicConnection)
    {
        _quicConnection = quicConnection;
    }

    /// <summary>
    /// The handler loop: drives the connection until it closes, invoking <paramref name="handler"/>
    /// once per completed request. Owns the handler's connection ref (DecRef on exit).
    /// </summary>
    public async Task RunAsync(Func<H3Request, H3Response> handler)
    {
        try
        {
            while (true)
            {
                QuicRecvSnapshot snap = await _quicConnection.ReadAsync();

                if (_h3 == 0 && !_fatal && !TrySetup())
                {
                    _fatal = true;
                }

                while (_quicConnection.TryGetItem(in snap, out QuicRecvRing.Item item))
                {
                    if (!_fatal)
                    {
                        Feed(in item);
                    }
                    _quicConnection.ReturnItem(in item);
                }

                if (!_fatal)
                {
                    DispatchReady(handler);
                    Drain();
                }

                if (snap.IsClosed || _fatal)
                {
                    break;
                }
                _quicConnection.ResetRead();
            }
        }
        finally
        {
            _quicConnection.DecRef();
            Dispose();
        }
    }

    // Open the server's uni streams (control + QPACK enc/dec) and stand up nghttp3. Runs on the
    // first post-handshake wake; the SETTINGS preface rides out on this wake's Drain.
    private unsafe bool TrySetup()
    {
        long ctrl = _quicConnection.OpenUniStream();
        long qenc = _quicConnection.OpenUniStream();
        long qdec = _quicConnection.OpenUniStream();
        if (ctrl < 0 || qenc < 0 || qdec < 0)
        {
            Console.Error.WriteLine("[ioxide.h3] failed to open control/QPACK uni streams");
            return false;
        }

        _self = GCHandle.Alloc(this);
        var callbacks = new Nghttp3.Callbacks
        {
            OnBeginHeaders = &CbBeginHeaders,
            OnHeader       = &CbHeader,
            OnEndHeaders   = &CbEndHeaders,
            OnData         = &CbData,
            OnEndStream    = &CbEndStream,
        };

        _h3 = Nghttp3.ih3_server_new(callbacks, (void*)GCHandle.ToIntPtr(_self));
        if (_h3 == 0)
        {
            Console.Error.WriteLine("[ioxide.h3] engine init failed");
            return false;
        }

        int rv = Nghttp3.ih3_bind_streams(_h3, ctrl, qenc, qdec);
        if (rv != 0)
        {
            Console.Error.WriteLine($"[ioxide.h3] bind streams failed: {Nghttp3.StrError(rv)}");
            return false;
        }
        return true;
    }

    private unsafe void Feed(in QuicRecvRing.Item item)
    {
        // Lifecycle items: mirror the QUIC stream state into nghttp3 so a cancelled request is
        // torn down on both sides instead of half-ignored (a dangling stream keeps per-stream
        // state alive and can feed the peer a confusing half-open response).
        if (item.Kind != QuicStreamEvent.Data)
        {
            _requests.Remove(item.StreamId);
            int erv = item.Kind switch
            {
                QuicStreamEvent.Reset       => Nghttp3.ih3_shutdown_stream_read(_h3, item.StreamId),
                QuicStreamEvent.StopSending => Nghttp3.ih3_shutdown_stream_write(_h3, item.StreamId),
                _                           => Nghttp3.ih3_close_stream(_h3, item.StreamId, item.AppError),
            };
            if (erv < 0)
            {
                Console.Error.WriteLine($"[ioxide.h3] stream {item.Kind} handling failed: {Nghttp3.StrError(erv)}");
                _fatal = true;
            }
            return;
        }

        long rv;
        if (item.Buf is not null)
        {
            fixed (byte* p = item.Buf)
            {
                rv = Nghttp3.ih3_read_stream(_h3, item.StreamId, p, (nuint)item.Len, item.Fin ? 1 : 0);
            }
        }
        else
        {
            rv = Nghttp3.ih3_read_stream(_h3, item.StreamId, null, 0, item.Fin ? 1 : 0);
        }

        if (rv < 0)
        {
            Console.Error.WriteLine($"[ioxide.h3] read_stream failed: {Nghttp3.StrError((int)rv)}");
            _fatal = true;
        }
    }

    private void DispatchReady(Func<H3Request, H3Response> handler)
    {
        for (int i = 0; i < _ready.Count && !_fatal; i++)
        {
            long sid = _ready[i];
            if (!_requests.Remove(sid, out H3Request? req))
            {
                continue;
            }
            req.Freeze();   // arena is final - materialize the public memories

            H3Response resp;
            try
            {
                resp = handler(req);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[ioxide.h3] request handler faulted: {e.GetBaseException().Message}");
                resp = new H3Response { Status = 500 };
            }

            Submit(sid, resp);
        }
        _ready.Clear();
    }

    private unsafe void Submit(long streamId, H3Response resp)
    {
        byte[] headers = PackHeaders(resp, out int headersLen);
        int rv;
        fixed (byte* ph = headers)
        fixed (byte* pb = resp.Body.Span)
        {
            rv = Nghttp3.ih3_submit_response(_h3, streamId, ph, (nuint)headersLen,
                pb, (nuint)resp.Body.Length);
        }
        ArrayPool<byte>.Shared.Return(headers);
        if (rv != 0)
        {
            Console.Error.WriteLine($"[ioxide.h3] submit_response failed: {Nghttp3.StrError(rv)}");
            _fatal = true;
        }
    }

    // Pump nghttp3's egress (SETTINGS, QPACK prefaces, response frames) into the QUIC engine.
    private unsafe void Drain()
    {
        fixed (byte* p = _egress)
        {
            while (true)
            {
                long sid;
                int fin;
                long n = Nghttp3.ih3_writev(_h3, &sid, &fin, p, (nuint)_egress.Length);
                if (n < 0)
                {
                    Console.Error.WriteLine($"[ioxide.h3] writev failed: {Nghttp3.StrError((int)n)}");
                    _fatal = true;
                    return;
                }
                if (n == 0 && sid == -1)
                {
                    return;
                }
                _quicConnection.SendStream(sid, _egress.AsSpan(0, (int)n), fin != 0);
            }
        }
    }

    // [u16 namelen][name][u16 valuelen][value]... (little-endian u16, written and read on the
    // same host) - ":status" first, content-length appended when a body is present and the
    // handler didn't set one. Names are lowercased as they're copied (h3 wire requirement);
    // numbers go through Utf8Formatter - no strings anywhere. The buffer is pooled: nghttp3
    // copies the entries during submit (NGHTTP3_NV_FLAG_NONE), so it's returned right after.
    private static byte[] PackHeaders(H3Response resp, out int written)
    {
        int cap = (4 + 7 + 3) + (4 + 14 + 20);   // :status + a worst-case content-length
        foreach ((ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value) in resp.Headers)
        {
            cap += 4 + name.Length + value.Length;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(cap);
        int w = 0;
        Span<byte> num = stackalloc byte[20];

        static void U16(byte[] b, ref int w, int x)
        {
            b[w++] = (byte)x;
            b[w++] = (byte)(x >> 8);
        }

        U16(buf, ref w, 7);
        ":status"u8.CopyTo(buf.AsSpan(w));
        w += 7;
        Utf8Formatter.TryFormat(resp.Status, num, out int numLen);
        U16(buf, ref w, numLen);
        num[..numLen].CopyTo(buf.AsSpan(w));
        w += numLen;

        bool hasContentLength = false;
        foreach ((ReadOnlyMemory<byte> nameM, ReadOnlyMemory<byte> valueM) in resp.Headers)
        {
            ReadOnlySpan<byte> name = nameM.Span;
            U16(buf, ref w, name.Length);
            int nameStart = w;
            foreach (byte b in name)
            {
                buf[w++] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
            }
            hasContentLength |= buf.AsSpan(nameStart, name.Length).SequenceEqual("content-length"u8);

            U16(buf, ref w, valueM.Length);
            valueM.Span.CopyTo(buf.AsSpan(w));
            w += valueM.Length;
        }

        if (!hasContentLength && resp.Body.Length > 0)
        {
            U16(buf, ref w, 14);
            "content-length"u8.CopyTo(buf.AsSpan(w));
            w += 14;
            Utf8Formatter.TryFormat(resp.Body.Length, num, out numLen);
            U16(buf, ref w, numLen);
            num[..numLen].CopyTo(buf.AsSpan(w));
            w += numLen;
        }

        written = w;
        return buf;
    }

    public void Dispose()
    {
        if (_h3 != 0)
        {
            Nghttp3.ih3_free(_h3);
            _h3 = 0;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    // --- unmanaged callbacks from the shim (reactor thread, inside ih3_read_stream) -----------

    private static unsafe H3Connection From(void* user)
        => (H3Connection)GCHandle.FromIntPtr((nint)user).Target!;

    [UnmanagedCallersOnly]
    private static unsafe void CbBeginHeaders(void* user, long streamId)
    {
        H3Connection h3Connection = From(user);
        h3Connection._requests[streamId] = new H3Request { StreamId = streamId };
    }

    [UnmanagedCallersOnly]
    private static unsafe void CbHeader(void* user, long streamId, byte* name, nuint nameLen, byte* value, nuint valueLen)
    {
        H3Connection c = From(user);
        if (!c._requests.TryGetValue(streamId, out H3Request? req))
        {
            return;
        }

        // Copy now - nghttp3 reclaims both buffers when this callback returns. Pseudo-headers
        // route by byte compare; nothing is decoded to text anywhere in the library.
        var n = new ReadOnlySpan<byte>(name, (int)nameLen);
        if (n.Length > 0 && n[0] == (byte)':')
        {
            (int Off, int Len) r = req.Append(value, (int)valueLen);
            if      (n.SequenceEqual(":method"u8))    req.MethodR = r;
            else if (n.SequenceEqual(":path"u8))      req.PathR = r;
            else if (n.SequenceEqual(":scheme"u8))    req.SchemeR = r;
            else if (n.SequenceEqual(":authority"u8)) req.AuthorityR = r;
            return;
        }

        (int Off, int Len) nr = req.Append(name, (int)nameLen);
        (int Off, int Len) vr = req.Append(value, (int)valueLen);
        req.HeaderRanges.Add((nr.Off, nr.Len, vr.Off, vr.Len));
    }

    [UnmanagedCallersOnly]
    private static unsafe void CbEndHeaders(void* user, long streamId, int fin)
    {
        // Body (if any) follows via CbData; completion is CbEndStream either way.
    }

    [UnmanagedCallersOnly]
    private static unsafe void CbData(void* user, long streamId, byte* data, nuint dataLen)
    {
        H3Connection c = From(user);
        if (!c._requests.TryGetValue(streamId, out H3Request? req))
        {
            return;
        }
        req.BodyBuffer ??= new MemoryStream();
        req.BodyBuffer.Write(new ReadOnlySpan<byte>(data, (int)dataLen));
    }

    [UnmanagedCallersOnly]
    private static unsafe void CbEndStream(void* user, long streamId)
    {
        H3Connection c = From(user);
        if (c._requests.TryGetValue(streamId, out H3Request? req) && !req.Complete)
        {
            req.Complete = true;
            c._ready.Add(streamId);
        }
    }
}
