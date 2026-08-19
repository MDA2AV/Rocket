#!/usr/bin/env bash
#
# Build the ClientHello parser harness.
#
# The parser is NOT copied here. It is extracted from the shim at build time, so there is exactly
# one source of truth and a rename is a loud build failure rather than a harness that quietly
# fuzzes a stale copy of the function.
#
#   bash fuzz/shim/build.sh          # clang -> libFuzzer, else gcc -> standalone sweep
#   CC=clang bash fuzz/shim/build.sh # force one
set -euo pipefail
cd "$(dirname "$0")/../.."

SHIM=src/protocols/ioxide.ngtcp2/native/ioxide_ngtcp2_shim.c
OUT=fuzz/shim/build
GEN=$OUT/iq_count_host_names.inc
FUNC='iq_count_host_names'

mkdir -p "$OUT"

# From the signature to the first line that is a closing brace in column 0. The shim is formatted
# consistently enough for that to be exact, and the guards below prove it rather than trusting it.
awk -v f="static int $FUNC" '
    index($0, f) == 1 { on = 1 }
    on { print }
    on && /^\}/ { exit }
' "$SHIM" > "$GEN"

[ -s "$GEN" ] || { echo "could not find '$FUNC' in $SHIM - has it been renamed or moved?" >&2; exit 1; }
grep -q '^}' "$GEN" || { echo "extraction of '$FUNC' did not reach a closing brace" >&2; exit 1; }
echo "==> extracted $FUNC ($(wc -l < "$GEN") lines) from the shim"

CC=${CC:-$(command -v clang || command -v gcc)}
[ -n "$CC" ] || { echo "no clang or gcc" >&2; exit 1; }

if "$CC" --version 2>/dev/null | grep -qi clang; then
    echo "==> clang: building a libFuzzer target"
    "$CC" -g -O1 -fsanitize=fuzzer,address,undefined \
        -I "$OUT" fuzz/shim/fuzz_client_hello.c -o "$OUT/fuzz_client_hello"
    echo "    run: $OUT/fuzz_client_hello fuzz/shim/corpus -max_total_time=60"
else
    echo "==> gcc: no libFuzzer, building the standalone sweep"
    echo "    (install clang for coverage-guided fuzzing; this path is corpus replay + seeded random)"
    "$CC" -g -O1 -fsanitize=address,undefined -DIQ_FUZZ_STANDALONE \
        -I "$OUT" fuzz/shim/fuzz_client_hello.c -o "$OUT/fuzz_client_hello"
    echo "    run: $OUT/fuzz_client_hello [iterations]"
fi
