"""Generate the docs' example panes from the Playground sources, so the two cannot drift.

Same contract as gen-docs-proxy-panes.py: each pane is the sample's real Program.cs with the
Playground.Shared indirection inlined, so what the page shows is a self-contained program.
"""
import html
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parent.parent

# pane slug -> (sample, title, packages, run lines, trailing note)
PANES = {
    "tls": (
        "Tls/Ktls", "kTLS &middot; raw ring", "ioxide",
        ["sudo modprobe tls        # kTLS needs the Linux 'tls' module + OpenSSL 3",
         "curl -k https://127.0.0.1:8443/"],
        "<b>Opt-in - and FULL kTLS</b>: both directions in the kernel, set right on the options. "
        "Kernel RX is experimental, so <code>Tls/Hybrid</code> in the repo is this server minus "
        "the <code>KernelRx</code> line - the half you would deploy today. The KernelTx line is what "
        "makes the <code>conn.Write</code> below legal: it puts PLAINTEXT into the slab and the "
        "kernel turns it into records. Without it the same call would put <b>cleartext on the "
        "wire</b>, which is why every other sample goes through <code>TlsSession.Write</code> - "
        "correct in either mode. Compare "
        "<label for=\"tab-tlsossl\" class=\"ex-jump\">openssl &middot; raw</label>."),
    "tlshybrid": (
        "Tls/Hybrid", "hybrid &middot; raw ring", "ioxide",
        ["sudo modprobe tls        # the kernel half still needs the module",
         "curl -k https://127.0.0.1:8443/"],
        "The deployable kernel mode: <b>kernel TX, OpenSSL RX</b>. The handler still writes "
        "plaintext - the kernel makes the records on send, which is what keeps <code>sendfile</code> "
        "and NIC offload reachable - while receive takes the well-trodden userspace path instead of "
        "kernel RX's experimental one. "
        "<label for=\"tab-tls\" class=\"ex-jump\">ktls &middot; raw</label> is this plus kernel "
        "receive; <label for=\"tab-tlsossl\" class=\"ex-jump\">openssl &middot; raw</label> is the "
        "default, with the kernel in neither direction."),
    "tlsossl": (
        "Tls/OpenSsl", "OpenSSL &middot; raw ring", "ioxide",
        ["curl -k https://127.0.0.1:8443/        # no modprobe needed"],
        "<b>The default.</b> No TLS ULP is attached at all - OpenSSL encrypts and decrypts, and the "
        "response goes through <code>TlsSession.Write</code>, which is correct whichever backend the "
        "session ended up with. That drops every constraint kTLS imposes: no kernel module, TLS 1.2, any "
        "ciphersuite, session resumption back, no handshake-alignment problem. What it gives up is "
        "<code>sendfile</code> and NIC offload. "
        "<b>It costs nothing measurable here</b> - against the plaintext baseline, 4 reactors, "
        "<code>wrk -t4 -c64</code>: 0.79&times; for both at a 64-byte response, 0.35&times; kTLS vs "
        "0.44&times; OpenSSL at 64 KiB. Which backend you pick matters far less than the cost of TLS."),
    "tlspipe": (
        "Tls/KtlsPipes", "kTLS &middot; pipes", "ioxide",
        ["sudo modprobe tls", "curl -k https://127.0.0.1:8443/"],
        "The same full-kTLS server behind an <code>IDuplexPipe</code>, for the frameworks that serve from "
        "one. Now compare <label for=\"tab-tlsosslpipe\" class=\"ex-jump\">openssl &middot; pipes</label>: "
        "its serve loop is <b>byte-identical</b> to this one. Over a pipe the backend is invisible, "
        "because <code>TlsConnectionDualPipe</code> composes its halves from the session rather than "
        "from configuration."),
    "tlsosslpipe": (
        "Tls/OpenSslPipes", "OpenSSL &middot; pipes", "ioxide",
        ["curl -k https://127.0.0.1:8443/        # no modprobe needed"],
        "Diff this against <label for=\"tab-tlspipe\" class=\"ex-jump\">ktls &middot; pipes</label> and "
        "the only functional difference is <code>KernelTx</code>; the rest is the banner and the log "
        "tag. That is the point of the pipe seam - <code>TlsConnectionDualPipe</code> pairs "
        "<code>TcpConnectionPipeReader</code> or <code>TlsDecryptingPipeReader</code> with "
        "<code>TcpConnectionPipeWriter</code> or <code>TlsEncryptingPipeWriter</code>, chosen from the "
        "<em>session</em>. It has to be the session and not the config, because a handshake that left a "
        "partial record keeps the userspace reader whatever was asked for."),
    "tlsmulti": (
        "Tls/MultiPort", "plaintext + TLS &middot; one server", "ioxide",
        ["curl  http://127.0.0.1:8080/", "curl -ks https://127.0.0.1:8081/"],
        "One server, two doors: plaintext on <code>:8080</code>, TLS on <code>:8081</code>, and ONE "
        "serve loop for both. The branch on <code>ListenerPort</code> is the entire difference - "
        "the TLS door builds a <code>TlsConnectionDualPipe</code>, the plaintext door pairs the "
        "connection's own reader and writer, and the loop reads an <code>IDuplexPipe</code> without "
        "knowing which it got. Ports come from <a href=\"learn/multiport.html\">multi-port</a>; "
        "TLS is the default backend, OpenSSL both ways."),

    # ── transport ────────────────────────────────────────────────────────────────────────────
    "tlsbcl": (
        "Tls/SslStream", "TLS &middot; SslStream", "ioxide",
        ["curl -k https://127.0.0.1:8443/"],
        "The third TLS backend, and the portable one: the BCL's <code>SslStream</code> over "
        "<code>TcpConnectionStream</code>. Fully managed, full-featured, and the slowest of the "
        "three, because the bytes are copied through a <code>Stream</code> both ways. Reach for it "
        "for <b>client certificates</b> - the ring-native path does not do mTLS - or anything else "
        "OpenSSL-on-the-ring does not expose; for TLS 1.2 and resumption the default already has "
        "you covered: <label for=\"tab-tlsossl\" class=\"ex-jump\">openssl &middot; raw</label>."),
    "big": (
        "Tcp/Big", "TCP &middot; large responses", "ioxide",
        ["curl -s http://127.0.0.1:8080/ | wc -c"],
        "The write path under a payload that does not fit the slab, and the knobs that shape it: "
        "<code>WriteSlabSize</code>, <code>WriteOverflow</code> (<code>Grow</code> reallocates, "
        "<code>Segmented</code> chains slabs into one SENDMSG) and <code>ZeroCopySend</code>, which "
        "only pays off once the response is large enough for the pinning to be worth it."),
    "hop": (
        "Tcp/Hop", "TCP &middot; leaving the reactor", "ioxide",
        ["curl http://127.0.0.1:8080/"],
        "The same server as <label for=\"tab-shared\" class=\"ex-jump\">raw &middot; shared ring</label>, "
        "except every request deliberately bounces off the reactor thread and back. It is here as the "
        "counter-example: this is what ioxide spends its design avoiding, and having it runnable makes "
        "the cost measurable rather than asserted."),
    "taskrun": (
        "Tcp/TaskRun", "TCP &middot; ordinary async", "ioxide",
        ["curl http://127.0.0.1:8080/"],
        "Proof that normal .NET async works inside a handler - <code>Task.Run</code>, "
        "<code>Task.Delay</code>, the thread pool - and that you come back to your reactor "
        "afterwards. The per-reactor <code>SynchronizationContext</code> is what makes that true, so "
        "connection and pool state stay single-threaded without a lock even when a handler wanders."),

    # ── protocols ────────────────────────────────────────────────────────────────────────────
    "h2tls": (
        "Http2/Tls", "HTTP/2 &middot; TLS &amp; ALPN", "ioxide + ioxide.nghttp2",
        ["curl -k --http2 https://127.0.0.1:8443/"],
        "How a browser actually reaches h2: over TLS, with the protocol chosen during the "
        "handshake. <code>Alpn = [\"h2\", \"http/1.1\"]</code> is an ordered preference, not a "
        "weighting - the server takes the first entry the client also offered, and this sample "
        "then branches on what was agreed, so one port serves both. That is why the h2c samples "
        "are the exception rather than the rule. TLS here is "
        "<b>OpenSSL both ways by default</b>; the <code>kernelTx</code>/<code>kernelRx</code> "
        "knobs at the top are what move either direction into the kernel."),
    "h2bcl": (
        "Http2/SslStream", "HTTP/2 &middot; over SslStream", "ioxide + ioxide.nghttp2",
        ["curl -k --http2 https://127.0.0.1:8443/"],
        "HTTP/2 over the BCL's <code>SslStream</code>, and the point is the ten-line "
        "<code>Stream</code>-to-<code>IDuplexPipe</code> adapter at the bottom. "
        "<code>Nghttp2Connection</code> takes a pipe, so the same HTTP/2 code runs over the ring "
        "directly, over ioxide's TLS, or over <code>SslStream</code> - the transport is a "
        "constructor argument, not a branch inside the protocol."),
    "h3buf": (
        "Http3/Buffered", "HTTP/3 &middot; buffered dispatch", "ioxide + ioxide.ngtcp2 + ioxide.nghttp3",
        ["curl --http3-only -k https://127.0.0.1:8443/"],
        "The same server as <label for=\"tab-h3\" class=\"ex-jump\">nghttp3</label> with the other "
        "dispatch mode - one method call is the whole difference. <b>Buffered</b> waits for "
        "end-of-stream, so the body is already in <code>request.Body</code> when your handler runs; "
        "the trade is that memory holds the whole body, which suits normal requests and not hostile "
        "uploads. <b>Streamed</b> runs you while the body is still arriving and credits the peer's "
        "flow-control window as you read, so memory is bound by one window instead."),
    "quicalpn": (
        "Quic/Alpn", "QUIC &middot; two protocols by ALPN", "ioxide + ioxide.ngtcp2 + ioxide.nghttp3",
        ["curl --http3-only -k https://127.0.0.1:8443/"],
        "One QUIC listener serving two protocols, chosen during the handshake: connections that "
        "negotiate <code>h3</code> get the HTTP/3 loop, anything else gets raw stream echo over the "
        "dual pipe. QUIC-only - <code>Tcp = null</code>, so the process opens no TCP listener at all."),
    "https": (
        "Clients/Https", "Client &middot; https origins", "ioxide + ioxide.httpclient",
        ["curl http://127.0.0.1:8080/get"],
        "TLS in the other direction: not terminating it for inbound connections but speaking it "
        "outbound, so a handler can reach an <code>https://</code> origin on the same ring that "
        "accepted the request. One <code>TlsClientContext</code> per reactor, shared by that "
        "reactor's pool. Verification is on by default - turning it off leaves the hop encrypted but "
        "<em>unauthenticated</em>, and a private CA belongs in <code>CaFile</code> instead."),
}

BANNER = re.compile(r"^// ─{5,}.*?^// ─{5,}\n\n", re.S | re.M)
KNOBS = re.compile(r"^// ── Knobs ─+\n.*?^// ─{20,}\n\n", re.S | re.M)


def inline(code: str) -> str:
    """Strip the harness plumbing so the pane is a program you can paste and run."""
    code = code.replace("using Playground.Shared;\n", "")

    # The knob block is the sample's configuration; keep the literals, drop the harness override
    # and the cert indirection that only exists because QuicCert generates one.
    code = re.sub(r"^Env\.Override\w*\([^)]*\);\n", "", code, flags=re.M)
    code = re.sub(r"\n\n(?=// ─{20,}\n\n)", "\n", code)   # the blank the override left behind

    # The "// Edit these..." paragraph explains the harness escape hatch, which the pane no longer
    # has. Drop the whole contiguous comment run rather than named lines, so a sample rewording it
    # does not silently leave a dangling reference to a call that is not there.
    code = re.sub(r"^// Edit these\.[^\n]*(?:\n//[^\n]*)*\n", "", code, flags=re.M)

    code = code.replace(
        "// A real PEM pair, or null to generate a self-signed localhost cert on first run.\n"
        "string? certOverride = null;\n"
        "string? keyOverride  = null;\n",
        'const string certPath = "cert.pem";   // any PEM pair\nconst string keyPath  = "key.pem";\n')
    code = re.sub(r"\(string certPath, string keyPath\) = QuicCert\.Ensure\(certOverride, keyOverride\);\n", "", code)

    # PLAYGROUND_INCREMENTAL is a bench escape hatch (per-connection recv rings); the pane shows the
    # sample's default - the shared ring - just like the other Env knobs collapse to their literals.
    code = re.sub(r'Env\.Flag\("PLAYGROUND_INCREMENTAL"\) \? new IncrementalOptions \{[^}]*\} : null', "null", code)

    code = BANNER.sub("", code)
    assert "Env." not in code and "QuicCert" not in code, \
        "harness plumbing survived: " + "; ".join(
            l.strip() for l in code.splitlines() if "Env." in l or "QuicCert" in l)
    return code.strip()


def build(slug: str) -> str:
    sample, title, packages, run, note = PANES[slug]
    body = inline((ROOT / f"Playground/{sample}/Program.cs").read_text())

    # One real command per package. This used to be a single line with the packages joined by
    # " + ", which read fine when every pane was TLS (one package) and became a command nobody
    # can paste as soon as the generator grew panes that need three.
    header = "\n".join(f"// dotnet add package {p.strip()}" for p in packages.split("+"))
    header += "\n" + "\n".join(f"//   {line}" for line in run)
    code = html.escape(f"{header}\n\n{body}", quote=False).replace("'", "&#x27;").replace('"', "&quot;")

    return (f'  <div class="pane pane-{slug}">\n'
            f'    <div class="pane-head">\n'
            f'      <h3>{title}</h3>\n'
            f'      <span class="pane-pkg">{packages}</span>\n'
            f'    </div>\n'
            f'<pre><code class="language-csharp">{code}</code></pre>\n'
            f'    <p class="ex-foot">{note}</p>\n'
            f'  </div>\n')


if __name__ == "__main__":
    index = ROOT / "docs/index.html"
    page = index.read_text()
    changed = 0

    for slug in PANES:
        marker = f'  <div class="pane pane-{slug}">'
        if marker not in page:
            raise SystemExit(f"pane-{slug} is not in docs/index.html - add its tab first")

        start = page.index(marker)
        end = page.index("  </div>\n", page.index("</code></pre>", start)) + len("  </div>\n")
        fresh = build(slug)
        if page[start:end] != fresh:
            page = page[:start] + fresh + page[end:]
            changed += 1

    if changed:
        index.write_text(page)
        print(f"rewrote {changed} of {len(PANES)} panes in docs/index.html")
    else:
        print("docs/index.html is already up to date")

    for slug, (sample, *_rest) in PANES.items():
        print(f"  pane-{slug:12} <- Playground/{sample}/Program.cs")
