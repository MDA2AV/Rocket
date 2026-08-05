using ioxide.httpclient;

namespace ioxide.httpclient;

/// <summary>Which protocol <see cref="RingHttpClient"/> should use for an origin.</summary>
public enum HttpProtocolPolicy
{
    /// <summary>Start on HTTP/1.1 and switch to HTTP/3 when the origin advertises it via Alt-Svc.</summary>
    Negotiate,

    /// <summary>HTTP/1.1 only - never upgrade, even if the origin advertises h3.</summary>
    Http1Only,

    /// <summary>HTTP/3 only - fail rather than fall back to HTTP/1.1.</summary>
    Http3Only,

    /// <summary>
    /// HTTP/2 cleartext (h2c) with prior knowledge, on <see cref="RingHttpClientOptions.Port"/>.
    ///
    /// Explicit rather than negotiated, and it has to be: h2 is chosen by ALPN during a TLS
    /// handshake, and this client has no TLS. An origin cannot advertise h2c the way it advertises
    /// h3 via Alt-Svc, so the caller must know the port speaks HTTP/2.
    /// </summary>
    Http2Only,
}

/// <summary>Origin and policy settings for <see cref="RingHttpClient"/>.</summary>
public sealed record RingHttpClientOptions
{
    /// <summary>IPv4 literal (DNS would block the reactor).</summary>
    public required string Host { get; init; }

    /// <summary>The origin's TCP port, for HTTP/1.1.</summary>
    public required ushort Port { get; init; }

    /// <summary>
    /// UDP port for HTTP/3. 0 means "whatever Alt-Svc advertises"; a non-zero value pins it and is
    /// required by <see cref="HttpProtocolPolicy.Http3Only"/>, which never sees an Alt-Svc header.
    /// </summary>
    public ushort Http3Port { get; init; }

    /// <summary>TLS server name for the QUIC handshake.</summary>
    public string ServerName { get; init; } = "localhost";

    public HttpProtocolPolicy Policy { get; init; } = HttpProtocolPolicy.Negotiate;

    /// <summary>Connections per protocol pool.</summary>
    public int PoolSize { get; init; } = 1;

    public int AcquireTimeoutMs { get; init; } = 5_000;

    /// <summary>How long to stay on HTTP/1.1 after an HTTP/3 attempt fails, before retrying h3.</summary>
    public int Http3CooldownMs { get; init; } = 30_000;
}

/// <summary>
/// One client for an origin, over whichever protocol that origin turns out to speak. Requests and
/// responses are the same types either way (<see cref="HttpClientRequest"/> /
/// <see cref="HttpClientResponse"/>), so switching protocol changes nothing for the caller:
///
/// <code>
/// reactor.OnStart = r => RingHttpClient.Start(r, new RingHttpClientOptions
/// {
///     Host = "10.0.0.7", Port = 8080,      // h1
///     Http3Port = 8443,                    // h3, once the origin advertises it
/// });
/// ...
/// using HttpClientResponse response = await client.GetAsync("/api/things"u8);
/// </code>
///
/// Negotiation follows the shape browsers use, because it is the only one that works without
/// knowing the origin up front: requests start on HTTP/1.1, and an <c>Alt-Svc: h3=":8443"</c> on any
/// response promotes the origin to HTTP/3 for subsequent requests. There is no ALPN path between
/// the two - h1 is TCP and h3 is QUIC, so they cannot share a handshake to negotiate within.
///
/// A failed h3 attempt demotes the origin back to HTTP/1.1 for
/// <see cref="RingHttpClientOptions.Http3CooldownMs"/>, so a broken or firewalled UDP path costs
/// one failure rather than every request. Both pools live on the caller's reactor, so responses
/// resume the awaiting handler inline on its own thread.
/// </summary>
/// <remarks>Reactor thread only: every member runs on the reactor that created the client.</remarks>
public sealed class RingHttpClient : IDisposable
{
    private readonly RingHttpClientOptions _options;
    private readonly HttpClientPool _http1;

    private readonly Reactor _reactor;
    private Http3ClientPool? _http3;      // created on first use, so h1-only origins never open QUIC
    private Http2ClientPool? _http2;      // h2c, opened only under Http2Only
    private ushort _http3Port;            // pinned by options, or learned from Alt-Svc
    private long _http3BlockedUntilMs;    // set by a failed h3 attempt (cooldown)
    private long _http3ExpiresAtMs;       // Alt-Svc ma= deadline for a LEARNED port; 0 = no expiry
    private bool _disposed;

    private RingHttpClient(Reactor reactor, RingHttpClientOptions options, HttpClientPool http1)
    {
        _reactor = reactor;
        _options = options;
        _http1 = http1;
        _http3Port = options.Http3Port;
    }

    /// <summary>Create the client and register it as a reactor service.</summary>
    public static RingHttpClient Start(Reactor reactor, RingHttpClientOptions options)
    {
        if (options.Policy == HttpProtocolPolicy.Http3Only && options.Http3Port == 0)
        {
            throw new ArgumentException(
                "Http3Only needs Http3Port set - there is no HTTP/1.1 response to carry an Alt-Svc advertisement.",
                nameof(options));
        }

        HttpClientPool http1 = HttpClientPool.Start(reactor, new HttpClientOptions
        {
            Host = options.Host,
            Port = options.Port,
            PoolSize = options.PoolSize,
            AcquireTimeoutMs = options.AcquireTimeoutMs,
        });

        var client = new RingHttpClient(reactor, options, http1);
        reactor.AddService(client);
        return client;
    }

    /// <summary>The protocol the next request would use, for diagnostics and tests.</summary>
    public string NextProtocol => _options.Policy switch
    {
        HttpProtocolPolicy.Http2Only => "h2c",
        _ => UseHttp3() ? "h3" : "http/1.1",
    };

    /// <summary>The h3 port in effect: pinned by options, or learned from Alt-Svc. 0 = none yet.</summary>
    public ushort NegotiatedHttp3Port => _http3Port;

    public ValueTask<HttpClientResponse> GetAsync(ReadOnlyMemory<byte> path)
        => SendAsync(new HttpClientRequest(HttpMethods.Get, path));

    public ValueTask<HttpClientResponse> GetAsync(string path)
        => SendAsync(new HttpClientRequest(HttpMethods.Get, path));

    public ValueTask<HttpClientResponse> PostAsync(ReadOnlyMemory<byte> path, ReadOnlyMemory<byte> body)
        => SendAsync(new HttpClientRequest(HttpMethods.Post, path) { Body = body });

    /// <summary>
    /// Send one request over the protocol currently selected for this origin. The response owns its
    /// bytes - dispose it when done.
    /// </summary>
    public ValueTask<HttpClientResponse> SendAsync(HttpClientRequest request)
    {
        if (_options.Policy == HttpProtocolPolicy.Http2Only)
        {
            return EnsureHttp2Pool().SendAsync(request);
        }
        return UseHttp3() ? SendOverHttp3Async(request) : SendOverHttp1Async(request);
    }

    private Http2ClientPool EnsureHttp2Pool()
        => _http2 ??= Http2ClientPool.Start(_reactor, new Http2ClientOptions
        {
            Host = _options.Host,
            Port = _options.Port,
            PoolSize = _options.PoolSize,
            AcquireTimeoutMs = _options.AcquireTimeoutMs,
        });

    // --- protocol selection --------------------------------------------------------------------

    private bool UseHttp3()
    {
        if (_options.Policy == HttpProtocolPolicy.Http1Only)
        {
            return false;
        }
        if (_options.Policy == HttpProtocolPolicy.Http3Only)
        {
            return true;
        }

        // Negotiate: only once an h3 endpoint is known, only outside a post-failure cooldown, and
        // only while the advertisement that taught us the port is still within its ma= lifetime.
        // An expired one falls back to HTTP/1.1, where the next response can re-advertise it.
        return _http3Port != 0 &&
               Environment.TickCount64 >= _http3BlockedUntilMs &&
               (_http3ExpiresAtMs == 0 || Environment.TickCount64 < _http3ExpiresAtMs);
    }

    private async ValueTask<HttpClientResponse> SendOverHttp1Async(HttpClientRequest request)
    {
        HttpClientResponse response = await _http1.SendAsync(request);
        ApplyAltSvc(response);
        return response;
    }

    private async ValueTask<HttpClientResponse> SendOverHttp3Async(HttpClientRequest request)
    {
        Http3ClientPool pool;
        try
        {
            pool = EnsureHttp3Pool();
        }
        catch (Exception e) when (_options.Policy != HttpProtocolPolicy.Http3Only)
        {
            // Failed before anything reached the origin (no pool, no connection), so resending is
            // safe whatever the method.
            Demote(e);
            return await SendOverHttp1Async(request);
        }

        try
        {
            return await pool.SendAsync(request);
        }
        catch (Exception e) when (_options.Policy != HttpProtocolPolicy.Http3Only)
        {
            // This request was already on the wire, so the origin may have processed it. Resending
            // it would execute a POST twice. Demote the origin either way, but only replay methods
            // that are safe to repeat; anything else surfaces the failure to the caller.
            Demote(e);
            if (!IsIdempotent(request.Method.Span))
            {
                throw;
            }
            return await SendOverHttp1Async(request);
        }
    }

    /// <summary>
    /// Track what the origin currently advertises. Consulted on EVERY HTTP/1.1 response, not only
    /// while no port is known: an origin that moves or retires its h3 endpoint says so in the
    /// header, and a client that stops listening after the first advertisement retries a dead port
    /// for its whole life.
    ///
    /// Only in Negotiate mode with an unpinned port. A configured Http3Port and the Http3Only and
    /// Http1Only policies are the caller's explicit choice and must not be overridden by a header.
    /// </summary>
    private void ApplyAltSvc(HttpClientResponse response)
    {
        if (_options.Policy != HttpProtocolPolicy.Negotiate || _options.Http3Port != 0 ||
            !response.TryGetHeader("alt-svc"u8, out ReadOnlyMemory<byte> header))
        {
            return;
        }

        AltSvc advertisement = ParseAltSvc(header.Span);

        if (advertisement.Clear)
        {
            RetireHttp3("the origin sent Alt-Svc: clear");
            return;
        }

        if (!advertisement.Advertises)
        {
            return;   // nothing about h3 in this header - leave what is in effect alone
        }

        // A moved endpoint: the pool holds the old port, so it has to go with it.
        if (_http3Port != 0 && advertisement.Port != _http3Port)
        {
            RetireHttp3($"the origin moved h3 to port {advertisement.Port}");
        }

        _http3Port = advertisement.Port;
        _http3ExpiresAtMs = Environment.TickCount64 + (advertisement.MaxAgeSeconds * 1000);
    }

    /// <summary>
    /// Drop the learned endpoint and the pool built around it. In-flight h3 requests on that pool
    /// fail; being h3 failures they demote and, when the method allows it, replay over HTTP/1.1.
    /// </summary>
    private void RetireHttp3(string reason)
    {
        if (_http3Port == 0 && _http3 is null)
        {
            return;
        }

        Console.Error.WriteLine($"[ringhttpclient] {_options.Host}: dropping h3 on :{_http3Port} - {reason}");

        // DETACH FIRST, dispose second. Disposing the pool closes its connections, and those
        // completions resume the waiting requests INLINE - so arbitrary caller code runs inside
        // the Dispose call below. Nulling afterwards would let that code observe a half-torn-down
        // client: with Http3CooldownMs at 0 a resumed caller re-entering SendAsync still passes
        // UseHttp3, EnsureHttp3Pool hands back the pool currently being disposed, and picking a
        // connection from it mutates the very list Dispose is enumerating - or opens a fresh QUIC
        // connection against an engine that is freed a moment later.
        Http3ClientPool? retiring = _http3;
        _http3 = null;
        _http3Port = 0;
        _http3ExpiresAtMs = 0;

        retiring?.Dispose();
    }

    // A UDP path that is blocked, or an origin that stopped serving h3: stay on HTTP/1.1 for the
    // cooldown so one failure does not become one per request. Logged once per demotion rather
    // than once per request - under load the per-request form is thousands of lines.
    private void Demote(Exception cause)
    {
        bool alreadyDemoted = Environment.TickCount64 < _http3BlockedUntilMs;
        _http3BlockedUntilMs = Environment.TickCount64 + _options.Http3CooldownMs;

        if (!alreadyDemoted)
        {
            Console.Error.WriteLine(
                $"[ringhttpclient] {_options.Host}:{_http3Port} h3 failed ({cause.Message}); " +
                $"using http/1.1 for {_options.Http3CooldownMs} ms");
        }
    }

    // Safe to repeat per RFC 9110 section 9.2.2. POST and PATCH are deliberately absent.
    private static bool IsIdempotent(ReadOnlySpan<byte> method)
        => method.SequenceEqual("GET"u8) || method.SequenceEqual("HEAD"u8) ||
           method.SequenceEqual("PUT"u8) || method.SequenceEqual("DELETE"u8) ||
           method.SequenceEqual("OPTIONS"u8) || method.SequenceEqual("TRACE"u8);

    private Http3ClientPool EnsureHttp3Pool()
    {
        if (_http3 is not null)
        {
            return _http3;
        }

        _http3 = Http3ClientPool.Start(_reactor, new Http3ClientOptions
        {
            Host = _options.Host,
            Port = _http3Port,
            ServerName = _options.ServerName,
            PoolSize = _options.PoolSize,
            AcquireTimeoutMs = _options.AcquireTimeoutMs,
        });
        return _http3;
    }

    // --- Alt-Svc -------------------------------------------------------------------------------

    /// <summary>
    /// What an Alt-Svc value says about this origin's h3 endpoint: an advertisement with a
    /// lifetime, an explicit retraction, or nothing we can use.
    /// </summary>
    internal readonly record struct AltSvc(bool Clear, ushort Port, long MaxAgeSeconds)
    {
        /// <summary>No h3 advertisement and no retraction - leave whatever is in effect alone.</summary>
        internal static AltSvc None => new(false, 0, DefaultMaxAgeSeconds);

        internal bool Advertises => !Clear && Port != 0;
    }

    /// <summary>RFC 7838 section 3.1: ma is optional and defaults to 24 hours.</summary>
    private const long DefaultMaxAgeSeconds = 86_400;

    /// <summary>
    /// Ceiling on an advertised lifetime, ~68 years. Far past any real ma=, and small enough that
    /// TickCount64 + seconds * 1000 cannot wrap - which would turn "cache this for a very long
    /// time" into an expiry in the past and disable h3 permanently.
    /// </summary>
    private const long MaxAgeSecondsLimit = int.MaxValue;

    /// <summary>
    /// Pull the h3 port out of an Alt-Svc value, e.g. <c>h3=":8443"; ma=86400, h3-29=":8443"</c>.
    /// Kept as the "is there a port we can use" question; <see cref="ParseAltSvc"/> is the full
    /// answer, including retraction and lifetime.
    /// </summary>
    internal static bool TryParseH3Port(ReadOnlySpan<byte> value, out ushort port)
    {
        AltSvc parsed = ParseAltSvc(value);
        port = parsed.Port;
        return parsed.Advertises;
    }

    /// <summary>
    /// Parse an Alt-Svc value. Only <c>h3</c> on the same host is honoured - an alternative host
    /// would need its own pool and its own certificate check, so it is ignored rather than
    /// followed.
    /// </summary>
    internal static AltSvc ParseAltSvc(ReadOnlySpan<byte> value)
    {
        // "clear" retracts every advertisement for the origin (RFC 7838 section 3).
        if (value.Trim((byte)' ').SequenceEqual("clear"u8))
        {
            return new AltSvc(Clear: true, Port: 0, MaxAgeSeconds: 0);
        }

        while (!value.IsEmpty)
        {
            int comma = value.IndexOf((byte)',');
            ReadOnlySpan<byte> entry = comma < 0 ? value : value[..comma];
            value = comma < 0 ? default : value[(comma + 1)..];

            int equals = entry.IndexOf((byte)'=');
            if (equals < 0)
            {
                continue;
            }

            if (!entry[..equals].Trim((byte)' ').SequenceEqual("h3"u8))
            {
                continue;   // h3-29 and friends are drafts we do not speak
            }

            ReadOnlySpan<byte> authority = entry[(equals + 1)..];
            long maxAge = DefaultMaxAgeSeconds;
            int semicolon = authority.IndexOf((byte)';');
            if (semicolon >= 0)
            {
                maxAge = ParseMaxAge(authority[(semicolon + 1)..]);
                authority = authority[..semicolon];
            }
            authority = authority.Trim((byte)' ').Trim((byte)'"');

            int colon = authority.LastIndexOf((byte)':');
            if (colon < 0)
            {
                continue;
            }

            // A host before the colon means a different origin - not ours to connect to.
            if (colon != 0)
            {
                continue;
            }

            if (ushort.TryParse(authority[(colon + 1)..], out ushort parsed) && parsed != 0)
            {
                return new AltSvc(Clear: false, parsed, maxAge);
            }
        }

        return AltSvc.None;
    }

    // "ma=86400; persist=1" - the parameters after an alternative's authority. Anything we cannot
    // read leaves the RFC default in place rather than expiring the advertisement early.
    private static long ParseMaxAge(ReadOnlySpan<byte> parameters)
    {
        while (!parameters.IsEmpty)
        {
            int semicolon = parameters.IndexOf((byte)';');
            ReadOnlySpan<byte> parameter = semicolon < 0 ? parameters : parameters[..semicolon];
            parameters = semicolon < 0 ? default : parameters[(semicolon + 1)..];

            int equals = parameter.IndexOf((byte)'=');
            if (equals < 0 || !parameter[..equals].Trim((byte)' ').SequenceEqual("ma"u8))
            {
                continue;
            }

            ReadOnlySpan<byte> raw = parameter[(equals + 1)..].Trim((byte)' ').Trim((byte)'"');
            if (long.TryParse(raw, out long seconds) && seconds >= 0)
            {
                // Saturate. The value is whatever the origin typed, and the caller turns it into
                // TickCount64 + seconds * 1000 - so a big enough one overflows and lands in the
                // PAST, which reads as "expired" and disables h3 for the life of the client. An
                // origin asking for a very long lifetime must not get the opposite of one.
                return Math.Min(seconds, MaxAgeSecondsLimit);
            }
        }

        return DefaultMaxAgeSeconds;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _http3?.Dispose();
        _http2?.Dispose();
        _http1.Dispose();
    }
}
