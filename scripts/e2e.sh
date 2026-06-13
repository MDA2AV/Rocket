#!/usr/bin/env bash
#
# Launch pg + redis sidecars (docker), build, run the ioxide e2e suite, then a wrk load test.
# Idempotent and self-contained: it spins its own containers and removes them afterwards.
#
#   scripts/e2e.sh                      # full run, load test against pg-shared
#   LOAD_MODE=redis-shared scripts/e2e.sh
#   LOAD_MODE=raw-shared LOAD_DUR=20s scripts/e2e.sh
#   KEEP=1 scripts/e2e.sh               # leave the sidecars running afterwards
#
set -uo pipefail
cd "$(dirname "$0")/.."

PG_PORT=${PG_PORT:-5432}
REDIS_PORT=${REDIS_PORT:-6379}
LOAD_MODE=${LOAD_MODE:-pg-shared}
LOAD_DUR=${LOAD_DUR:-10s}
LOAD_CONNS=${LOAD_CONNS:-256}

log() { printf '\n==> %s\n' "$1"; }

# Start a container if one by that name isn't already running.
ensure() {
    local name=$1 image=$2 ports=$3
    shift 3
    if docker ps --format '{{.Names}}' | grep -qx "$name"; then
        return
    fi
    docker rm -f "$name" >/dev/null 2>&1
    docker run -d --name "$name" -p "$ports" "$@" "$image" >/dev/null
}

log "starting pg + redis sidecars"
ensure ioxide-e2e-pg postgres:18 "${PG_PORT}:5432" \
    -e POSTGRES_USER=bench -e POSTGRES_DB=bench -e POSTGRES_HOST_AUTH_METHOD=trust
ensure ioxide-e2e-redis redis:7-alpine "${REDIS_PORT}:6379"

log "waiting for pg"
for _ in $(seq 1 60); do
    docker exec ioxide-e2e-pg pg_isready -q -U bench >/dev/null 2>&1 && break
    sleep 1
done

log "waiting for redis"
for _ in $(seq 1 30); do
    docker exec ioxide-e2e-redis redis-cli ping >/dev/null 2>&1 && break
    sleep 1
done
docker exec ioxide-e2e-redis redis-cli set ioxide:bench 42 >/dev/null 2>&1   # seed for redis-shared

export EXAMPLES_PG_PORT=$PG_PORT EXAMPLES_PG_DB=bench EXAMPLES_REDIS_PORT=$REDIS_PORT

log "building (Release)"
if ! dotnet build -c Release ioxide.slnx >/dev/null; then
    echo "build failed"
    exit 1
fi

log "running e2e suite"
./Tests/bin/Release/net10.0/ioxide.e2e
e2e=$?

log "load test: $LOAD_MODE, $LOAD_CONNS connections for $LOAD_DUR"
bin=./Examples/bin/Release/net10.0/Examples
pkill -x Examples >/dev/null 2>&1
sleep 0.5
EXAMPLES_MODE=$LOAD_MODE "$bin" >/tmp/ioxide-load.log 2>&1 &
for _ in $(seq 1 20); do
    ss -ltn 2>/dev/null | grep -q ':8080' && break
    sleep 0.5
done
sleep 1

url="http://127.0.0.1:8080/"
[ "${LOAD_MODE#tls-}" != "$LOAD_MODE" ] && url="https://127.0.0.1:8080/"
if command -v wrk >/dev/null; then
    wrk -c"$LOAD_CONNS" -t"$(nproc)" -d"$LOAD_DUR" "$url"
else
    echo "wrk not installed - skipping the load test"
fi
pkill -x Examples >/dev/null 2>&1

if [ "${KEEP:-0}" != 1 ]; then
    log "removing sidecars (run with KEEP=1 to keep them)"
    docker rm -f ioxide-e2e-pg ioxide-e2e-redis >/dev/null 2>&1
fi

log "e2e suite exit code: $e2e"
exit $e2e
