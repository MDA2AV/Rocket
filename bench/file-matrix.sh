#!/usr/bin/env bash
# Static files: does reading the file straight into the write slab still pay once TLS is on?
#
#   slab     conn.ReadFileAsync puts the file in the write slab at the current tail - no reader
#            buffer, no copy. IORING_OP_READ lands the bytes where the next send picks them up.
#   buffer   read into a pooled native buffer, then move those bytes into the slab.
#
# Crossed with the transport, because what is legal follows from what the slab must contain:
#
#   none      the slab IS the wire.                        both paths legal
#   ktls      the slab holds plaintext, kernel encrypts.   both paths legal  <- the pairing at issue
#   openssl   the slab must hold ciphertext.               buffer ONLY, by construction
#
# METHOD - one saturated reactor. This is not a detail. With several reactors that are not pegged,
# the reactor threads spend time polling an idle ring, that time lands in utime/stime, and CPU per
# request comes out inflated and the ratio between arms compressed. An earlier version of this
# script ran 4 unsaturated reactors and reported an 11% slab win where the single-reactor method
# had measured 24%. One reactor, driven past saturation, verified above MIN_UTIL - that is the
# number that means something.
#
# Loopback only: no DMA, no NIC offload. Fine for this question - what is being measured is an
# eliminated memory copy, not a hardware path.
set -uo pipefail
cd "$(dirname "$0")/.."

REACTORS=${REACTORS:-1}
DURATION=${DURATION:-10}
CONNS=${CONNS:-64}
THREADS=${THREADS:-8}      # load generator threads; must outrun the single reactor
PASSES=${PASSES:-2}
SIZES=${SIZES:-"4096 65536"}
MIN_UTIL=${MIN_UTIL:-90}   # reject a cell whose reactor was not actually saturated (% of one core)
DIR=${DIR:-/tmp/ioxide-bench-assets}
OUT=${OUT:-/tmp/file-matrix}
BIN=Playground/Clients/File/bin/Release/net11.0/Playground.Clients.File

mkdir -p "$OUT" "$DIR"
SERVER_PID=""

stop_server() {
  [[ -z "$SERVER_PID" ]] && return 0
  kill "$SERVER_PID" 2>/dev/null
  for _ in $(seq 40); do kill -0 "$SERVER_PID" 2>/dev/null || break; sleep 0.1; done
  kill -9 "$SERVER_PID" 2>/dev/null
  wait "$SERVER_PID" 2>/dev/null
  SERVER_PID=""
}
trap stop_server EXIT

cpu_ticks() { awk '{print $14 + $15}' "/proc/$1/stat" 2>/dev/null || echo 0; }

declare -A BEST CPU NOTE

cell() {   # cell <label> <port> <buffered> <tlsmode> <size>
  local label=$1 port=$2 buffered=$3 mode=$4 size=$5
  local scheme=http; [[ $mode != none ]] && scheme=https
  local url="$scheme://127.0.0.1:$port/f.bin"
  local tag="${label//[^a-z0-9]/_}_$size"

  # SO_REUSEPORT will happily let a leaked server from an earlier run co-bind this port and take
  # half the load. Refuse to start rather than measure two servers at once.
  if ss -ltnp "sport = :$port" 2>/dev/null | grep -q LISTEN; then
    echo "  !! port $port already held - skipping"; NOTE["$label|$size"]="port held"; return
  fi

  env PLAYGROUND_DIR="$DIR" PLAYGROUND_REACTORS="$REACTORS" PLAYGROUND_PORT="$port" \
      PLAYGROUND_FILE_BUFFERED="$buffered" PLAYGROUND_FILE_TLS="$mode" \
      "$BIN" >"$OUT/server.log" 2>&1 &
  SERVER_PID=$!

  local up=0
  for _ in $(seq 100); do
    kill -0 "$SERVER_PID" 2>/dev/null || break
    curl -sk -o /dev/null --max-time 1 "$url" && { up=1; break; }
    sleep 0.1
  done
  if [[ $up == 0 ]]; then
    echo "  !! $label never answered"; tail -3 "$OUT/server.log"
    NOTE["$label|$size"]="no answer"; stop_server; return
  fi

  # Exactly one process may be listening here.
  local listeners; listeners=$(ss -ltnp "sport = :$port" 2>/dev/null | grep -c LISTEN)
  if [[ "$listeners" != 1 ]]; then
    echo "  !! $label has $listeners listeners on $port"; NOTE["$label|$size"]="listeners=$listeners"
    stop_server; return
  fi

  # The arms must serve the SAME bytes, or the comparison is of two different workloads.
  local got; got=$(curl -sk --max-time 3 "$url" | wc -c)
  if [[ "$got" != "$size" ]]; then
    echo "  !! $label served $got bytes, expected $size"; NOTE["$label|$size"]="bytes=$got"
    stop_server; return
  fi

  wrk -t"$THREADS" -c"$CONNS" -d5s "$url" >/dev/null 2>&1          # warm: JIT, slabs, handshakes

  local t0 t1 rps; t0=$(cpu_ticks "$SERVER_PID")
  wrk -t"$THREADS" -c"$CONNS" -d"${DURATION}s" --latency "$url" >"$OUT/$tag.txt" 2>&1
  t1=$(cpu_ticks "$SERVER_PID")
  rps=$(grep -oP 'Requests/sec:\s+\K[\d.]+' "$OUT/$tag.txt" | head -1)
  stop_server
  rps=${rps:-0}

  # A run with failed requests is not a throughput number. wrk reports both separately.
  local bad; bad=$(grep -oP 'Non-2xx or 3xx responses: \K\d+' "$OUT/$tag.txt" | head -1)
  local errs; errs=$(grep -oP 'Socket errors:.*' "$OUT/$tag.txt" | head -1)
  if [[ -n "${bad:-}" && "${bad:-0}" != 0 ]]; then
    echo "  !! $label had $bad non-2xx responses - discarding"; NOTE["$label|$size"]="non2xx=$bad"; return
  fi
  if [[ -n "${errs:-}" ]]; then
    echo "  !! $label $errs - discarding"; NOTE["$label|$size"]="socket errors"; return
  fi

  # Saturation check: CPU seconds burned over wall seconds, as a percentage of ONE core.
  local util; util=$(awk -v t="$((t1 - t0))" -v d="$DURATION" 'BEGIN{printf "%.0f", (t/100.0)/d*100}')
  if (( util < MIN_UTIL )); then
    echo "  !! $label reactor only ${util}% busy (need ${MIN_UTIL}%) - raise CONNS/THREADS"
    NOTE["$label|$size"]="util=${util}%"; return
  fi

  local us; us=$(awk -v t="$((t1 - t0))" -v r="$rps" -v d="$DURATION" \
    'BEGIN{ if (r>0) printf "%.2f", (t/100.0)/(r*d)*1e6; else print "0" }')

  if awk -v a="$rps" -v b="${BEST["$label|$size"]:-0}" 'BEGIN{exit !(a>b)}'; then
    BEST["$label|$size"]=$rps; CPU["$label|$size"]=$us
  fi
  printf '    %-18s %-6s %11s req/s  %6s us cpu/req  util %s%%\n' "$label" "$size" "$rps" "$us" "$util"
}

for pass in $(seq "$PASSES"); do
  echo "== pass $pass/$PASSES  ($REACTORS reactor, $CONNS conns, ${DURATION}s, saturated)"
  for size in $SIZES; do
    head -c "$size" /dev/urandom | base64 | head -c "$size" > "$DIR/f.bin"
    cell none-slab      9201 0 none    "$size"
    cell none-buffer    9202 1 none    "$size"
    cell ktls-slab      9203 0 ktls    "$size"
    cell ktls-buffer    9204 1 ktls    "$size"
    cell openssl-buffer 9205 1 openssl "$size"
  done
done

echo
echo "== best of $PASSES passes"
printf '   %-16s %11s  %10s  %s\n' arm req/s cpu/req note
for size in $SIZES; do
  echo "-- file $size bytes"
  for arm in none-slab none-buffer ktls-slab ktls-buffer openssl-buffer; do
    printf '   %-16s %11.0f  %8s us  %s\n' "$arm" "${BEST["$arm|$size"]:-0}" \
      "${CPU["$arm|$size"]:-0}" "${NOTE["$arm|$size"]:-}"
  done
  awk -v a="${BEST["none-slab|$size"]:-0}"   -v b="${BEST["none-buffer|$size"]:-0}" \
      -v c="${CPU["none-slab|$size"]:-0}"    -v d="${CPU["none-buffer|$size"]:-0}" \
      'BEGIN{ if (a>0&&b>0) printf "     cleartext: slab is %.2fx rps, %.2fx cpu/req\n", a/b, c/d }'
  awk -v a="${BEST["ktls-slab|$size"]:-0}"   -v b="${BEST["ktls-buffer|$size"]:-0}" \
      -v c="${CPU["ktls-slab|$size"]:-0}"    -v d="${CPU["ktls-buffer|$size"]:-0}" \
      'BEGIN{ if (a>0&&b>0) printf "     ktls:      slab is %.2fx rps, %.2fx cpu/req\n", a/b, c/d }'
  awk -v a="${BEST["ktls-slab|$size"]:-0}"   -v b="${BEST["openssl-buffer|$size"]:-0}" \
      -v c="${CPU["ktls-slab|$size"]:-0}"    -v d="${CPU["openssl-buffer|$size"]:-0}" \
      'BEGIN{ if (a>0&&b>0) printf "     TLS best:  ktls+slab is %.2fx rps, %.2fx cpu/req vs openssl+buffer\n", a/b, c/d }'
done
