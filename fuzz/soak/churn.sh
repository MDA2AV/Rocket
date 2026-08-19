#!/usr/bin/env bash
#
# Connection churn: build a QUIC connection, use it, tear it down, repeat.
#
#   bash fuzz/soak/churn.sh                 # ~200k requests, reconnecting every 4
#   bash fuzz/soak/churn.sh 2000000 1       # a new connection for every single request
#
# Setup and teardown is where a QUIC server leaks, and none of it shows on a server that answers
# one request well. What is being watched for: a connection id route that outlives its connection,
# a native handle nothing frees, a timer that keeps firing on a connection already gone. Those
# appear as RSS that only climbs, or as a server that slowly stops routing.
#
# This is a soak, not a gate. It is not deterministic, it takes minutes, and a clean run says only
# that this run was clean. When it does break, the reproduction belongs in tests/ as a named case.
set -euo pipefail
cd "$(dirname "$0")/../.."

REQUESTS=${1:-200000}
PER_CONN=${2:-4}
SAMPLE=${SAMPLE:-Playground/Http3/ManagedBuffered}
PORT=${PORT:-18443}
H3X=${H3X:-$(command -v h3x || echo /home/diogo/h3x/build/h3x)}

[ -x "$H3X" ] || { echo "h3x not found (set H3X=/path/to/h3x) - it is the only client here that reconnects on demand" >&2; exit 1; }

dotnet build -c Release "$SAMPLE" >/dev/null
PLAYGROUND_QUIC_PORT=$PORT dotnet run -c Release --no-build --project "$SAMPLE" >/tmp/ioxide-churn.log 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT

for _ in $(seq 40); do
    sleep 0.25
    kill -0 $SERVER 2>/dev/null || { echo "the sample died on startup:"; tail -5 /tmp/ioxide-churn.log; exit 1; }
    grep -q "reactors" /tmp/ioxide-churn.log && break
done

rss() { awk '/VmRSS/ {print $2}' "/proc/$1/status" 2>/dev/null || echo 0; }

BEFORE=$(rss "$SERVER")
echo "== $REQUESTS requests, a new connection every $PER_CONN, rss ${BEFORE} kB"

"$H3X" -k -n "$REQUESTS" -m 1 --reconnect "$PER_CONN" --socket-per-conn \
    "https://127.0.0.1:$PORT/" 2>&1 | tail -6

sleep 2
AFTER=$(rss "$SERVER")
echo "== rss ${BEFORE} kB -> ${AFTER} kB (delta $((AFTER - BEFORE)) kB over $((REQUESTS / PER_CONN)) connections)"

# Still serving is the assertion that matters: a transport that stopped routing looks identical to
# one that is merely idle until you ask it for something.
if "$H3X" -k -n 1 "https://127.0.0.1:$PORT/" >/dev/null 2>&1; then
    echo "== still serving after the churn"
else
    echo "== FAILED: the server stopped serving after the churn"; exit 1
fi
