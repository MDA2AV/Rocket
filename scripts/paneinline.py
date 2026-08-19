"""The Playground -> pane inliner, shared by both generators.

A pane is the sample's real Program.cs with the Playground.Shared indirection resolved, so what
the page shows is a program you can paste and run. This lives in one place because it used to
live in two, and the copy in the proxy generator silently fell behind the samples.
"""
import re

BANNER = re.compile(r"^// ─{5,}.*?^// ─{5,}\n\n", re.S | re.M)
KNOBS = re.compile(r"^// ── Knobs ─+\n.*?^// ─{20,}\n\n", re.S | re.M)


# Env.Int("NAME", 8080) -> 8080. The default IS the sample's configuration; the lookup around it
# only exists so bench/run.sh and the Dockerfile can drive a sample from outside, and a pane is
# meant to be pasted and run. Samples that declare literal knobs and call Env.Override are handled
# above; this is for the ones that still read inline, so the page can be generated from ALL of them
# without first rewriting samples whose env names CI depends on.
ENV_DEFAULTS = {"StrOrNull": "null", "Flag": "false"}


class _Suffixed:
    """A regex match with the file-name suffix the rule wants, so both rules share one formatter."""

    def __init__(self, match, suffix: str):
        self._m = match
        self.suffix = suffix

    def group(self, key):
        return self.suffix if key == "suffix" else self._m.group(key)


def inline_env(code: str) -> str:
    while (m := re.search(r"Env\.(\w+)\(", code)) is not None:
        func = m.group(1)
        # Walk to the matching close paren - a default can itself contain calls and parens, e.g.
        # Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount).
        depth, i = 0, m.end() - 1
        for i in range(m.end() - 1, len(code)):
            if code[i] == "(":
                depth += 1
            elif code[i] == ")":
                depth -= 1
                if depth == 0:
                    break
        else:
            raise SystemExit(f"unbalanced Env.{func}( in sample")

        args = code[m.end():i]
        if func in ENV_DEFAULTS:
            value = ENV_DEFAULTS[func]
        else:
            # Split on the top-level comma: everything after it is the default.
            depth, comma = 0, -1
            for j, ch in enumerate(args):
                if ch in "([":
                    depth += 1
                elif ch in ")]":
                    depth -= 1
                elif ch == "," and depth == 0:
                    comma = j
                    break
            if comma < 0:
                raise SystemExit(f"Env.{func} has no default to inline: {args}")
            value = args[comma + 1:].strip()

        code = code[:m.start()] + value + code[i + 1:]
    return code


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

    # QuicCert.Ensure only exists because the harness generates a cert on first run. Drop the
    # override declarations wherever they appear and turn the assignment itself into the two
    # literals, so the pane names a PEM pair the reader can point at.
    code = code.replace(
        "// A real PEM pair, or null to generate a self-signed localhost cert on first run.\n"
        "string? certOverride = null;\n"
        "string? keyOverride  = null;\n", "")
    # Any spelling of the assignment - a sample that only needs a cert in some modes writes it as
    # a conditional, and one that never had override knobs calls Ensure inline.
    code = re.sub(r"\(string certPath, string keyPath\) =[^;]*QuicCert\.Ensure[^;]*;\n",
                  'const string certPath = "cert.pem";   // any PEM pair\n'
                  'const string keyPath  = "key.pem";\n', code, flags=re.S)

    # The SNI and rotation samples mint a pair per host name, and a second pair for the same name
    # to renew to. A pane cannot call the generator, so each becomes the paths a reader would
    # actually have: whatever their ACME client writes per name.
    def _pair(match: "re.Match[str]") -> str:
        first, second, arg, suffix = match.group(1), match.group(2), match.group(3).strip(), match.group("suffix")
        if arg.startswith('"') and arg.endswith('"'):          # a literal name: "localhost"
            stem = arg[1:-1]
            return f'(string {first}, string {second}) = ("{stem}{suffix}.pem", "{stem}{suffix}.key");'
        return (f'(string {first}, string {second}) = '
                f'($"{{{arg}}}{suffix}.pem", $"{{{arg}}}{suffix}.key");')

    code = re.sub(r"\(string (\w+), string (\w+)\) = QuicCert\.EnsureRenewed\(([^()]*)\);",
                  lambda m: _pair(_Suffixed(m, "-renewed")), code)
    code = re.sub(r"\(string (\w+), string (\w+)\) = QuicCert\.EnsureNamed\(([^()]*)\);",
                  lambda m: _pair(_Suffixed(m, "")), code)

    # PLAYGROUND_INCREMENTAL is a bench escape hatch (per-connection recv rings); the pane shows the
    # sample's default - the shared ring - just like the other Env knobs collapse to their literals.
    code = re.sub(r'Env\.Flag\("PLAYGROUND_INCREMENTAL"\) \? new IncrementalOptions \{[^}]*\} : null', "null", code)

    code = inline_env(code)

    # SampleAssets writes a demo index.html so the sample has something to serve on a bare
    # machine. A pane that calls it does not compile, because the type is not in the pane.
    code = re.sub(r"^SampleAssets\.\w+\([^;]*\);[^\n]*\n", "", code, flags=re.M)

    # Comments that point at Playground.Shared describe machinery the pane does not contain.
    code = re.sub(r"^//[^\n]*(?:QuicCert|SampleAssets|Playground\.Shared|Playground/Shared)[^\n]*\n",
                  "", code, flags=re.M)

    code = BANNER.sub("", code)

    # Everything Playground.Shared provides has to be gone, or the pane is not a program anyone
    # can paste and run - which is the only thing a pane is for.
    # "QuicCert." with the dot: QuicCertificate is a real ioxide.ngtcp2 type and belongs in a pane,
    # while every use of the Playground helper is a static call through it.
    leaked = [n for n in ("Env.", "QuicCert.", "SampleAssets") if n in code]
    assert not leaked, "harness plumbing survived: " + "; ".join(
        l.strip() for l in code.splitlines() if any(n in l for n in leaked))
    return code.strip()


