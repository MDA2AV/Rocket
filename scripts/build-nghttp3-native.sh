#!/usr/bin/env bash
#
# Build the self-contained HTTP/3 native bundle for ioxide.nghttp3: nghttp3 (sans-I/O H3 + QPACK,
# ngtcp2's companion library) statically linked behind a small C shim into ONE shared library
# with no external dependencies beyond libc. nghttp3 does no I/O and no crypto - the transport
# is whatever QuicConnection the bridge rides on.
#
#   scripts/build-nghttp3-native.sh                 # build with pinned refs below
#   NGHTTP3_REF=v1.6.0 scripts/build-nghttp3-native.sh
#
set -euo pipefail
cd "$(dirname "$0")/.."

NGHTTP3_REF=${NGHTTP3_REF:-master}
WORK=${WORK:-/tmp/ioxide-h3-native}
OUT=src/protocols/ioxide.nghttp3/runtimes/linux-x64/native

rm -rf "$WORK" && mkdir -p "$WORK" "$OUT"
cd "$WORK"

echo "==> cloning nghttp3 ($NGHTTP3_REF)"
git clone --depth 1 --branch "$NGHTTP3_REF" --recurse-submodules --shallow-submodules \
    https://github.com/ngtcp2/nghttp3 >/dev/null 2>&1 || \
    git clone --depth 1 --recurse-submodules --shallow-submodules https://github.com/ngtcp2/nghttp3

echo "==> building nghttp3 (static, PIC)"
cmake -S nghttp3 -B nghttp3/build -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DENABLE_LIB_ONLY=ON \
    -DENABLE_SHARED_LIB=OFF -DENABLE_STATIC_LIB=ON >/dev/null
cmake --build nghttp3/build -j"$(nproc)" >/dev/null

echo "==> compiling the C# facade shim"
SHIM="$OLDPWD/src/protocols/ioxide.nghttp3/native/ioxide_nghttp3_shim.c"
gcc -c -O2 -fPIC -o shim.o "$SHIM" \
    -Inghttp3/lib/includes -Inghttp3/build/lib/includes

echo "==> linking libioxide_nghttp3.so"
gcc -shared -o libioxide_nghttp3.so shim.o \
    -Wl,--whole-archive nghttp3/build/lib/libnghttp3.a -Wl,--no-whole-archive \
    -Wl,--no-undefined

echo "==> verifying"
ldd libioxide_nghttp3.so | grep -vE 'vdso|libc\.|ld-linux' && {
    echo "unexpected dependency"; exit 1; } || true
[ "$(nm -D --defined-only libioxide_nghttp3.so | grep -c ' ih3_')" -ge 8 ] || {
    echo "shim exports missing"; exit 1; }

cd - >/dev/null
cp "$WORK/libioxide_nghttp3.so" "$OUT/libioxide_nghttp3.so"
echo "==> done: $OUT/libioxide_nghttp3.so ($(du -h "$OUT/libioxide_nghttp3.so" | cut -f1))"
