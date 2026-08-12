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
    "H2ToH1": ("h2 &rarr; h1", "HTTP/2 in &middot; HTTP/1.1 out",
               "ioxide + ioxide.http2 + ioxide.httpclient",
               [
                'PLAYGROUND_PORT=8444 dotnet run --project Playground/Tls/Ktls   # a TLS origin',
                'curl -k --http2 https://127.0.0.1:8443/']),
    "H3ToH1": ("h3 &rarr; h1", "HTTP/3 in &middot; HTTP/1.1 out",
               "ioxide + ioxide.ngtcp2 + ioxide.nghttp3 + ioxide.httpclient",
               [
                'PLAYGROUND_PORT=8444 dotnet run --project Playground/Tls/Ktls   # a TLS origin',
                'curl --http3-only -k https://127.0.0.1:8443/']),
}

# The trailing note on each pane: what this combination is actually for.
NOTES = {
    "H1ToH1": "The baseline, and the one to read first. Everything else in this section is this "
              "program with one type changed.",
    "H2ToH1": "The classic edge: clients get multiplexing and header compression, the origin keeps "
              "speaking what it already speaks. The one combination here whose pool must size for "
              "concurrency - a hundred h2 streams need a hundred h1 connections, because h1 has no "
              "multiplexing to borrow.",
    "H3ToH1": "QUIC-only frontend: <code>Tcp = null</code>, so every TCP socket this process owns is "
              "outbound. The h3 server and the h1 client share the reactor and nothing else.",
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
