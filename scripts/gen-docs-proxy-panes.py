"""Generate the docs' proxy panes from the Playground sources, so the two cannot drift.

Each pane is the sample's real Program.cs with the Playground.Shared indirection inlined
(Env.Int(...) -> the literal default), so what the page shows is a self-contained program.
"""
import html
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from paneinline import inline

ROOT = pathlib.Path(__file__).resolve().parent.parent

# slug -> (menu label, pane title, packages, how to run it)
COMBOS = {
    "H1ToH1": ("h1 &rarr; h1", "HTTP/1.1 in &middot; HTTP/1.1 out",
               "ioxide + ioxide.httpclient",
               [
                'PLAYGROUND_PORT=8444 dotnet run --project Playground/Tls/Ktls   # a TLS origin',
                'curl -k https://127.0.0.1:8443/']),
    "H1ToH2": ("h1 &rarr; h2", "HTTP/1.1 in &middot; HTTP/2 out",
               "ioxide + ioxide.httpclient",
               [
                'PLAYGROUND_PORT=8444 dotnet run --project Playground/Http2/Tls  # an h2-over-TLS origin',
                'curl -k https://127.0.0.1:8443/']),
    "H1ToH3": ("h1 &rarr; h3", "HTTP/1.1 in &middot; HTTP/3 out",
               "ioxide + ioxide.httpclient",
               [
                'dotnet run --project Playground/Http3/Nghttp3Request          # h3 origin on udp :8443',
                'PLAYGROUND_UPSTREAM_PORT=8443 dotnet run --project Playground/Proxy/H1ToH3',
                'curl -k https://127.0.0.1:8443/']),
    "H2ToH1": ("h2 &rarr; h1", "HTTP/2 in &middot; HTTP/1.1 out",
               "ioxide + ioxide.nghttp2 + ioxide.httpclient",
               [
                'PLAYGROUND_PORT=8444 dotnet run --project Playground/Tls/Ktls   # a TLS origin',
                'curl -k --http2 https://127.0.0.1:8443/']),
    "H2ToH2": ("h2 &rarr; h2", "HTTP/2 in &middot; HTTP/2 out",
               "ioxide + ioxide.nghttp2 + ioxide.httpclient",
               [
                'PLAYGROUND_PORT=8444 dotnet run --project Playground/Http2/Tls  # an h2-over-TLS origin',
                'curl -k --http2 https://127.0.0.1:8443/']),
    "H2ToH3": ("h2 &rarr; h3", "HTTP/2 in &middot; HTTP/3 out",
               "ioxide + ioxide.nghttp2 + ioxide.httpclient",
               [
                'dotnet run --project Playground/Http3/Nghttp3Request          # h3 origin on udp :8443',
                'PLAYGROUND_UPSTREAM_PORT=8443 dotnet run --project Playground/Proxy/H2ToH3',
                'curl -k --http2 https://127.0.0.1:8443/']),
    "H3ToH1": ("h3 &rarr; h1", "HTTP/3 in &middot; HTTP/1.1 out",
               "ioxide + ioxide.ngtcp2 + ioxide.nghttp3 + ioxide.httpclient",
               [
                'PLAYGROUND_PORT=8444 dotnet run --project Playground/Tls/Ktls   # a TLS origin',
                'curl --http3-only -k https://127.0.0.1:8443/']),
    "H3ToH2": ("h3 &rarr; h2", "HTTP/3 in &middot; HTTP/2 out",
               "ioxide + ioxide.ngtcp2 + ioxide.nghttp3 + ioxide.httpclient",
               [
                'PLAYGROUND_PORT=8444 dotnet run --project Playground/Http2/Tls  # an h2-over-TLS origin',
                'curl --http3-only -k https://127.0.0.1:8443/']),
    "H3ToH3": ("h3 &rarr; h3", "HTTP/3 in &middot; HTTP/3 out",
               "ioxide + ioxide.ngtcp2 + ioxide.nghttp3 + ioxide.httpclient",
               [
                'PLAYGROUND_QUIC_PORT=8444 dotnet run --project Playground/Http3/Nghttp3Request',
                'curl --http3-only -k https://127.0.0.1:8443/']),
}

# The trailing note on each pane: what this combination is actually for.
NOTES = {
    "H1ToH1": "The baseline, and the one to read first. Everything else in this section is this "
              "program with one type changed.",
    "H1ToH2": "Identical to <b>h1 &rarr; h1</b> above except for the pool type and its size. h1 needs "
              "an upstream connection per in-flight request; h2 multiplexes, so <code>PoolSize = 1</code> "
              "carries every concurrent request on one socket.",
    "H1ToH3": "Note what the config does <em>not</em> contain: no <code>Quic</code>, no UDP ports, no "
              "certificate. Being an HTTP/3 <em>client</em> requires no HTTP/3 server - the first connect "
              "opens an ephemeral UDP socket on this reactor's ring and replies route back by connection ID.",
    "H2ToH1": "The classic edge: clients get multiplexing and header compression, the origin keeps "
              "speaking what it already speaks. The one combination here whose pool must size for "
              "concurrency - a hundred h2 streams need a hundred h1 connections, because h1 has no "
              "multiplexing to borrow.",
    "H2ToH2": "The narrowest proxy in this set: two sockets per reactor no matter how many requests "
              "are in flight. Two independent nghttp2 sessions are involved and they share nothing - "
              "HPACK state is per-connection, so a proxy always re-encodes. “Just splice the frames” "
              "is not a shortcut that exists.",
    "H2ToH3": "TCP in, QUIC out - the client half of a migration where the origin moved to HTTP/3 and "
              "the clients did not. Both hops are completions on the same ring, one from a TCP recv and "
              "one from a UDP recv, and both resume inline.",
    "H3ToH1": "QUIC-only frontend: <code>Tcp = null</code>, so every TCP socket this process owns is "
              "outbound. The h3 server and the h1 client share the reactor and nothing else.",
    "H3ToH2": "The same program as <b>h3 &rarr; h1</b> with the pool swapped - which is the whole point "
              "of the matrix. Concurrent h3 requests fan into one h2-over-TLS upstream connection "
              "instead of taking a keep-alive connection each.",
    "H3ToH3": "QUIC on both sides, and the upstream connections share the serving socket: one fd, both "
              "directions. Nothing else in this set collapses that far.",
}

BANNER = re.compile(r"^// ─{5,}.*?^// ─{5,}\n\n", re.S | re.M)


def build(slug: str) -> str:
    label, title, packages, run = COMBOS[slug]
    src = (ROOT / f"Playground/Proxy/{slug}/Program.cs").read_text()

    body = inline(src)

    header = "// dotnet add package " + " ".join(packages.split(" + "))
    header += "\n" + "\n".join(f"//   {line}" for line in run)

    code = html.escape(f"{header}\n\n{body}", quote=False).replace("'", "&#x27;").replace('"', "&quot;")

    return (
        f'  <div class="pane pane-px{slug.lower()}">\n'
        f'    <div class="pane-head">\n'
        f'      <h3>{title}</h3>\n'
        f'      <span class="pane-pkg">{packages}</span>\n'
        f'    </div>\n'
        f'<pre><code class="language-csharp">{code}</code></pre>\n'
        f'    <p class="ex-foot">{NOTES[slug]}</p>\n'
        f'  </div>\n')


if __name__ == "__main__":
    out = "".join(build(slug) for slug in COMBOS)
    index = ROOT / "docs/index.html"
    page = index.read_text()

    first = page.index('  <div class="pane pane-pxh1toh1">')
    last = page.index('  <div class="pane pane-http">')
    if page[first:last] == out:
        print("docs/index.html is already up to date")
    else:
        index.write_text(page[:first] + out + page[last:])
        print(f"rewrote {len(COMBOS)} panes in docs/index.html")

    for slug in COMBOS:
        print(f"  pane-px{slug.lower():10} <- Playground/Proxy/{slug}/Program.cs")
