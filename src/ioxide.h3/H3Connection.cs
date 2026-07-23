using System.Runtime.InteropServices;
using System.Text;
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
    private readonly QuicConnection _conn;
    private nint _h3;
    private GCHandle _self;
    private bool _fatal;

    private readonly Dictionary<long, H3Request> _requests = new();
    private readonly List<long> _ready = [];
    private readonly byte[] _egress = new byte[16 * 1024];

    public H3Connection(QuicConnection conn)
    {
        _conn = conn;
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
                QuicRecvSnapshot snap = await _conn.ReadAsync();

                if (_h3 == 0 && !_fatal && !TrySetup())
                {
                    _fatal = true;
                }

                while (_conn.TryGetItem(in snap, out QuicRecvRing.Item item))
                {
                    if (!_fatal)
                    {
                        Feed(in item);
                    }
                    _conn.ReturnItem(in item);
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
                _conn.ResetRead();
            }
        }
        finally
        {
            _conn.DecRef();
            Dispose();
        }
    }

    // Open the server's uni streams (control + QPACK enc/dec) and stand up nghttp3. Runs on the
    // first post-handshake wake; the SETTINGS preface rides out on this wake's Drain.
    private unsafe bool TrySetup()
    {
        long ctrl = _conn.OpenUniStream();
        long qenc = _conn.OpenUniStream();
        long qdec = _conn.OpenUniStream();
        if (ctrl < 0 || qenc < 0 || qdec < 0)
        {
            Console.Error.WriteLine("[ioxide.h3] failed to open control/QPACK uni streams");
            return false;
        }

        _self = GCHandle.Alloc(this);
        var cbs = new Nghttp3.Callbacks
        {
            OnBeginHeaders = &CbBeginHeaders,
            OnHeader       = &CbHeader,
            OnEndHeaders   = &CbEndHeaders,
            OnData         = &CbData,
            OnEndStream    = &CbEndStream,
        };

        _h3 = Nghttp3.ih3_server_new(cbs, (void*)GCHandle.ToIntPtr(_self));
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
            req.Body = req.BodyBuffer?.ToArray() ?? [];
            req.BodyBuffer = null;

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
        byte[] headers = PackHeaders(resp);
        int rv;
        fixed (byte* ph = headers)
        fixed (byte* pb = resp.Body)
        {
            rv = Nghttp3.ih3_submit_response(_h3, streamId, ph, (nuint)headers.Length,
                pb, (nuint)resp.Body.Length);
        }
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
                _conn.SendStream(sid, _egress.AsSpan(0, (int)n), fin != 0);
            }
        }
    }

    // [u16 namelen][name][u16 valuelen][value]... - ":status" first, content-length appended when
    // a body is present and the handler didn't set one.
    private static byte[] PackHeaders(H3Response resp)
    {
        var buf = new MemoryStream(256);

        void Add(string name, string value)
        {
            byte[] n = Encoding.ASCII.GetBytes(name);
            byte[] v = Encoding.ASCII.GetBytes(value);
            Span<byte> len = stackalloc byte[2];
            BitConverter.TryWriteBytes(len, (ushort)n.Length);
            buf.Write(len);
            buf.Write(n);
            BitConverter.TryWriteBytes(len, (ushort)v.Length);
            buf.Write(len);
            buf.Write(v);
        }

        Add(":status", resp.Status.ToString());

        bool hasContentLength = false;
        foreach ((string name, string value) in resp.Headers)
        {
            Add(name.ToLowerInvariant(), value);
            hasContentLength |= name.Equals("content-length", StringComparison.OrdinalIgnoreCase);
        }
        if (!hasContentLength && resp.Body.Length > 0)
        {
            Add("content-length", resp.Body.Length.ToString());
        }

        return buf.ToArray();
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
        H3Connection c = From(user);
        c._requests[streamId] = new H3Request { StreamId = streamId };
    }

    [UnmanagedCallersOnly]
    private static unsafe void CbHeader(void* user, long streamId, byte* name, nuint nameLen, byte* value, nuint valueLen)
    {
        H3Connection c = From(user);
        if (!c._requests.TryGetValue(streamId, out H3Request? req))
        {
            return;
        }

        string n = Encoding.ASCII.GetString(name, (int)nameLen);
        string v = Encoding.UTF8.GetString(value, (int)valueLen);
        switch (n)
        {
            case ":method":    req.Method = v; break;
            case ":path":      req.Path = v; break;
            case ":scheme":    req.Scheme = v; break;
            case ":authority": req.Authority = v; break;
            default:           req.Headers.Add((n, v)); break;
        }
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
