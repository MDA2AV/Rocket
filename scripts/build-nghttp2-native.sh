#!/usr/bin/env bash
#
# Build the self-contained HTTP/2 native bundle for ioxide.nghttp2: nghttp2 (sans-I/O HTTP/2 +
# HPACK) statically linked behind a small C shim into ONE shared library with no external
# dependencies beyond libc. nghttp2 does no I/O and no TLS - bytes arrive through
# nghttp2_session_mem_recv and leave through nghttp2_session_mem_send, so the transport is whatever
# TcpConnection the bridge rides on.
#
#   scripts/build-nghttp2-native.sh                 # build with pinned refs below
#   NGHTTP2_REF=v1.64.0 scripts/build-nghttp2-native.sh
#
set -euo pipefail
cd "$(dirname "$0")/.."

NGHTTP2_REF=${NGHTTP2_REF:-master}
WORK=${WORK:-/tmp/ioxide-h2-native}
OUT=src/bindings/ioxide.nghttp2/runtimes/linux-x64/native

rm -rf "$WORK" && mkdir -p "$WORK" "$OUT"
cd "$WORK"

echo "==> cloning nghttp2 ($NGHTTP2_REF)"
git clone --depth 1 --branch "$NGHTTP2_REF" https://github.com/nghttp2/nghttp2 >/dev/null 2>&1 || \
    git clone --depth 1 https://github.com/nghttp2/nghttp2

echo "==> building nghttp2 (static, PIC)"
cmake -S nghttp2 -B nghttp2/build -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DENABLE_LIB_ONLY=ON \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON \
    -DENABLE_FAILMALLOC=OFF -DBUILD_TESTING=OFF >/dev/null
cmake --build nghttp2/build -j"$(nproc)" >/dev/null

echo "==> compiling the C# facade shim"
SHIM="$OLDPWD/src/bindings/ioxide.nghttp2/native/ioxide_nghttp2_shim.c"
gcc -c -O2 -fPIC -o shim.o "$SHIM" \
    -Inghttp2/lib/includes -Inghttp2/build/lib/includes

echo "==> linking libioxide_nghttp2.so"
gcc -shared -o libioxide_nghttp2.so shim.o \
    -Wl,--whole-archive nghttp2/build/lib/libnghttp2.a -Wl,--no-whole-archive \
    -Wl,--no-undefined

echo "==> verifying"
ldd libioxide_nghttp2.so | grep -vE 'vdso|libc\.|ld-linux' && {
    echo "unexpected dependency"; exit 1; } || true
[ "$(nm -D --defined-only libioxide_nghttp2.so | grep -c ' ih2_')" -ge 6 ] || {
    echo "shim exports missing"; exit 1; }

cd - >/dev/null
cp "$WORK/libioxide_nghttp2.so" "$OUT/libioxide_nghttp2.so"
echo "==> done: $OUT/libioxide_nghttp2.so ($(du -h "$OUT/libioxide_nghttp2.so" | cut -f1))"
