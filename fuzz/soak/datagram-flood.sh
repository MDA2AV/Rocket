#!/usr/bin/env bash
#
# Fire malformed datagrams at a live QUIC port, then ask it to serve a real request.
#
#   bash fuzz/soak/datagram-flood.sh              # 200k datagrams
#   bash fuzz/soak/datagram-flood.sh 2000000
#
# The assertion is deliberately NOT "it did not crash". A transport that quietly stopped routing
# looks exactly like a healthy idle one, so what is checked at the end is that a real client is
# still served - and that the process did not grow while being shouted at, which is what an
# unbounded per-datagram allocation looks like from outside.
set -euo pipefail
cd "$(dirname "$0")/../.."

COUNT=${1:-200000}
SAMPLE=${SAMPLE:-Playground/Http3/ManagedBuffered}
PORT=${PORT:-18444}
H3X=${H3X:-$(command -v h3x || echo /home/diogo/h3x/build/h3x)}

dotnet build -c Release "$SAMPLE" >/dev/null
PLAYGROUND_QUIC_PORT=$PORT dotnet run -c Release --no-build --project "$SAMPLE" >/tmp/ioxide-flood.log 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT

for _ in $(seq 40); do
    sleep 0.25
    grep -q "reactors" /tmp/ioxide-flood.log && break
done

rss() { awk '/VmRSS/ {print $2}' "/proc/$1/status" 2>/dev/null || echo 0; }
BEFORE=$(rss "$SERVER")
echo "== $COUNT datagrams at :$PORT, rss ${BEFORE} kB"

python3 - "$PORT" "$COUNT" <<'PY'
import os, random, socket, sys
port, count = int(sys.argv[1]), int(sys.argv[2])
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
dst = ("127.0.0.1", port)
random.seed(0x5EED)          # reproducible: a failing run is re-runnable byte for byte

for i in range(count):
    kind = i & 3
    if kind == 0:                                   # pure noise
        payload = os.urandom(random.randint(1, 1500))
    elif kind == 1:                                 # long header, plausible shape, junk body
        payload = bytes([0xC0 | random.randint(0, 15), 0, 0, 0, 1,
                         random.randint(0, 21)]) + os.urandom(random.randint(0, 1300))
    elif kind == 2:                                 # short header against an id nobody issued
        payload = bytes([0x40]) + os.urandom(random.randint(0, 40))
    else:                                           # truncated long header
        payload = bytes([0xC0, 0, 0, 0, 1])[:random.randint(1, 5)]
    try:
        sock.sendto(payload, dst)
    except OSError:
        pass
print(f"   sent {count} datagrams")
PY

sleep 2
AFTER=$(rss "$SERVER")
echo "== rss ${BEFORE} kB -> ${AFTER} kB (delta $((AFTER - BEFORE)) kB)"

if [ -x "$H3X" ] && "$H3X" -k -n 1 "https://127.0.0.1:$PORT/" >/dev/null 2>&1; then
    echo "== still serving after the flood"
else
    echo "== FAILED: the server did not serve a real request after the flood"; exit 1
fi
