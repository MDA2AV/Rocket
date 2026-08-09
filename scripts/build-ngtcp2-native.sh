#!/usr/bin/env bash
#
# Build the self-contained QUIC native bundle for ioxide.ngtcp2:
#   ngtcp2 (QUIC engine) + its picotls crypto backend + picotls (TLS 1.3 handshake),
# statically linked into ONE shared library whose only external dependency is
# libcrypto.so.3 (picotls uses OpenSSL's libcrypto primitives, so any OpenSSL 3.x
# works - no OpenSSL 3.5 QUIC API needed). Output lands in the package's
# runtimes/ dir so the nupkg carries everything.
#
#   scripts/build-ngtcp2-native.sh                 # build with pinned refs below
#   NGTCP2_REF=v1.14.0 scripts/build-ngtcp2-native.sh
#
set -euo pipefail
cd "$(dirname "$0")/.."

NGTCP2_REF=${NGTCP2_REF:-master}
PICOTLS_REF=${PICOTLS_REF:-master}
WORK=${WORK:-/tmp/ioxide-quic-native}
OUT=src/protocols/ioxide.ngtcp2/runtimes/linux-x64/native

rm -rf "$WORK" && mkdir -p "$WORK" "$OUT"
cd "$WORK"

echo "==> cloning picotls ($PICOTLS_REF) + ngtcp2 ($NGTCP2_REF)"
git clone --depth 1 --branch "$PICOTLS_REF" --recurse-submodules --shallow-submodules \
    https://github.com/h2o/picotls >/dev/null 2>&1 || \
    git clone --depth 1 --recurse-submodules --shallow-submodules https://github.com/h2o/picotls
git clone --depth 1 --branch "$NGTCP2_REF" https://github.com/ngtcp2/ngtcp2 >/dev/null 2>&1 || \
    git clone --depth 1 https://github.com/ngtcp2/ngtcp2

echo "==> building picotls (static, PIC)"
cmake -S picotls -B picotls/build -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DWITH_FUSION=OFF >/dev/null
cmake --build picotls/build -j"$(nproc)" >/dev/null

echo "==> building ngtcp2 + crypto_picotls (static, PIC, default visibility)"
# ngtcp2 pins hidden visibility on its static targets; the bundle needs the API exported.
sed -i 's/C_VISIBILITY_PRESET hidden/C_VISIBILITY_PRESET default/' \
    ngtcp2/lib/CMakeLists.txt ngtcp2/crypto/picotls/CMakeLists.txt
cmake -S ngtcp2 -B ngtcp2/build -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DBUILD_TESTING=OFF \
    -DENABLE_PICOTLS=ON -DENABLE_OPENSSL=OFF -DENABLE_GNUTLS=OFF \
    -DENABLE_SHARED_LIB=OFF -DENABLE_STATIC_LIB=ON \
    -DPICOTLS_INCLUDE_DIR="$WORK/picotls/include" \
    "-DPICOTLS_LIBRARIES=$WORK/picotls/build/libpicotls-openssl.a;$WORK/picotls/build/libpicotls-core.a;crypto" >/dev/null
cmake --build ngtcp2/build -j"$(nproc)" >/dev/null

echo "==> compiling the C# facade shim"
SHIM="$OLDPWD/src/protocols/ioxide.ngtcp2/native/ioxide_ngtcp2_shim.c"
gcc -c -O2 -fPIC -o shim.o "$SHIM" \
    -Ingtcp2/lib/includes -Ingtcp2/build/lib/includes \
    -Ingtcp2/crypto/includes -Ipicotls/include

echo "==> linking libioxide_ngtcp2.so"
gcc -shared -o libioxide_ngtcp2.so shim.o \
    -Wl,--whole-archive \
    ngtcp2/build/lib/libngtcp2.a \
    ngtcp2/build/crypto/picotls/libngtcp2_crypto_picotls.a \
    picotls/build/libpicotls-openssl.a \
    picotls/build/libpicotls-core.a \
    -Wl,--no-whole-archive -lcrypto -Wl,--no-undefined

echo "==> verifying"
ldd libioxide_ngtcp2.so | grep -vE 'vdso|libcrypto|libc\.|ld-linux' && {
    echo "unexpected dependency"; exit 1; } || true
[ "$(nm -D --defined-only libioxide_ngtcp2.so | grep -c ' ngtcp2_')" -gt 500 ] || {
    echo "ngtcp2 exports missing"; exit 1; }
[ "$(nm -D --defined-only libioxide_ngtcp2.so | grep -c ' ptls_')" -gt 100 ] || {
    echo "picotls exports missing"; exit 1; }
[ "$(nm -D --defined-only libioxide_ngtcp2.so | grep -c ' iq_')" -ge 13 ] || {
    echo "shim exports missing"; exit 1; }

cd - >/dev/null
cp "$WORK/libioxide_ngtcp2.so" "$OUT/libioxide_ngtcp2.so"
echo "==> done: $OUT/libioxide_ngtcp2.so ($(du -h "$OUT/libioxide_ngtcp2.so" | cut -f1))"
