#!/usr/bin/env bash
#
# Static-file benchmark for the ioxide `file` example.
#
# Uses the same fixtures and wrk rotation script as HttpArena
# (https://github.com/MDA2AV/HttpArena, data/static + requests/static-rotate.lua):
# it serves Examples/wwwroot and rotates GETs across the 20 /static/* assets with
# `Accept-Encoding: br;q=1, gzip;q=0.8` - HttpArena's `static` profile, which runs
#     wrk -t<threads> -c<conns> -d<dur> -s static-rotate.lua http://localhost:8080
# (pipeline 1; HttpArena sweeps conns 1024,4096,6800 pinned to a dedicated cpuset).
#
# Env overrides:
#   PORT      (default 8080)
#   DURATION  (default 10s)
#   THREADS   (default 8)            wrk worker threads
#   CONNS     (default 512)          comma-separated for a sweep, e.g. CONNS=1024,4096,6800
#
# For comparable numbers, pin the server and wrk to disjoint cores, e.g.:
#   taskset -c 0-15  EXAMPLES_MODE=file ... Examples      (server)
#   taskset -c 16-31 wrk ...                               (load)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WWW="$ROOT/Examples/wwwroot"
LUA="$ROOT/scripts/static-rotate.lua"
BIN="$ROOT/Examples/bin/Release/net10.0/Examples"

PORT="${PORT:-8080}"
DURATION="${DURATION:-10s}"
THREADS="${THREADS:-8}"
CONNS="${CONNS:-512}"

command -v wrk    >/dev/null 2>&1 || { echo "error: wrk not installed - https://github.com/wg/wrk"; exit 1; }
command -v dotnet >/dev/null 2>&1 || { echo "error: dotnet not found on PATH"; exit 1; }
[ -f "$LUA" ]        || { echo "error: missing $LUA"; exit 1; }
[ -d "$WWW/static" ] || { echo "error: missing $WWW/static (the HttpArena static set)"; exit 1; }

echo "==> building Examples (Release)"
dotnet build -c Release "$ROOT/Examples/Examples.csproj" >/dev/null

echo "==> starting ioxide file server on :$PORT  (serving $WWW)"
EXAMPLES_MODE=file EXAMPLES_FILE_DIR="$WWW" "$BIN" &
SRV=$!
trap 'kill "$SRV" 2>/dev/null || true; wait "$SRV" 2>/dev/null || true' EXIT

# wait for the listener (curl spaces its own retries - no foreground sleep)
curl --retry 30 --retry-delay 1 --retry-connrefused -fsS -o /dev/null "http://localhost:$PORT/static/reset.css" 2>/dev/null \
    || { echo "error: server never became ready on :$PORT"; exit 1; }

echo "==> warmup (3s)"
wrk -t"$THREADS" -c128 -d3s -s "$LUA" "http://localhost:$PORT" >/dev/null 2>&1 || true

IFS=',' read -ra CONN_LIST <<< "$CONNS"
for c in "${CONN_LIST[@]}"; do
    t=$(( THREADS < c ? THREADS : c ))
    echo
    echo "==> wrk -t$t -c$c -d$DURATION -s static-rotate.lua  (20 assets, Accept-Encoding: br;q=1, gzip;q=0.8)"
    wrk -t"$t" -c"$c" -d"$DURATION" -s "$LUA" "http://localhost:$PORT"
done

cat <<'NOTE'

note: ioxide.file serves the identity asset (e.g. GET /static/app.js -> app.js). It does
      not yet content-negotiate the precompressed .br/.gz variants, so the Accept-Encoding
      header is sent (matching HttpArena) but ignored server-side. The .br/.gz files are
      bundled as fixtures (identical to HttpArena's set) for parity / future negotiation.
NOTE
