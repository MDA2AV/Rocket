"""Generate the docs' TLS panes from the Playground sources, so the two cannot drift.

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
        "<b>Opt-in</b> - note the explicit <code>KernelTx = true</code>. TLS is OpenSSL in both "
        "directions unless you ask for this, and that line is what makes the <code>conn.Write</code> "
        "below legal: it puts PLAINTEXT into the slab and the kernel turns it into records. Without "
        "it the same call would put <b>cleartext on the wire</b>, which is why every other sample "
        "goes through <code>TlsSession.Write</code> - correct in either mode. Receive stays in "
        "userspace either way. Compare "
        "<label for=\"tab-tlsossl\" class=\"ex-jump\">openssl &middot; raw</label>."),
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
        "The same kTLS server behind an <code>IDuplexPipe</code>, for the frameworks that serve from "
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
        "<code>TcpConnectionPipeReader</code> or <code>TlsPumpPipeReader</code> with "
        "<code>TcpConnectionPipeWriter</code> or <code>TlsEncryptingPipeWriter</code>, chosen from the "
        "<em>session</em>. It has to be the session and not the config, because a handshake that left a "
        "partial record keeps the userspace reader whatever was asked for."),
}

BANNER = re.compile(r"^// ─{5,}.*?^// ─{5,}\n\n", re.S | re.M)
KNOBS = re.compile(r"^// ── Knobs ─+\n.*?^// ─{20,}\n\n", re.S | re.M)


def inline(code: str) -> str:
    """Strip the harness plumbing so the pane is a program you can paste and run."""
    code = code.replace("using Playground.Shared;\n", "")

    # The knob block is the sample's configuration; keep the literals, drop the harness override
    # and the cert indirection that only exists because QuicCert generates one.
    code = code.replace("\nEnv.Override(ref port, ref reactors, ref bodyBytes);\n", "")
    code = code.replace("\nEnv.Override(ref port, ref reactors);\n", "")
    code = re.sub(r"// Env\.Override exists only[^\n]*\n// when you copy this out[^\n]*\n", "", code)
    code = re.sub(r"// Edit these\. That is the whole mechanism[^\n]*\n", "", code)

    code = code.replace(
        "// A real PEM pair, or null to generate a self-signed localhost cert on first run.\n"
        "string? certOverride = null;\n"
        "string? keyOverride  = null;\n",
        'const string certPath = "cert.pem";   // any PEM pair\nconst string keyPath  = "key.pem";\n')
    code = re.sub(r"\(string certPath, string keyPath\) = QuicCert\.Ensure\(certOverride, keyOverride\);\n", "", code)

    code = BANNER.sub("", code)
    assert "Env." not in code and "QuicCert" not in code, \
        "harness plumbing survived: " + "; ".join(
            l.strip() for l in code.splitlines() if "Env." in l or "QuicCert" in l)
    return code.strip()


def build(slug: str) -> str:
    sample, title, packages, run, note = PANES[slug]
    body = inline((ROOT / f"Playground/{sample}/Program.cs").read_text())

    header = f"// dotnet add package {packages}      -- TLS ships in the core package\n"
    header += "\n".join(f"//   {line}" for line in run)
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
        print(f"rewrote {changed} of {len(PANES)} TLS panes in docs/index.html")
    else:
        print("docs/index.html is already up to date")

    for slug, (sample, *_rest) in PANES.items():
        print(f"  pane-{slug:12} <- Playground/{sample}/Program.cs")
