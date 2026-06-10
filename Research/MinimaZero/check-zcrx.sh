#!/usr/bin/env bash
# Checks whether the host can run MinimaZero (io_uring zero-copy recv).
# Read-only; no sudo needed. Run on your *target* host.
set +e

ok(){ echo "  [OK]  $1"; }; bad(){ echo "  [!!]  $1"; }; info(){ echo "  [..]  $1"; }

echo "1. Kernel (need >= 6.15)"
uname -r
KREL=$(uname -r | grep -oE '^[0-9]+\.[0-9]+'); KMAJ=${KREL%.*}; KMIN=${KREL#*.}
if [ "$KMAJ" -gt 6 ] || { [ "$KMAJ" -eq 6 ] && [ "$KMIN" -ge 15 ]; }; then
  ok "zcrx is in-tree on this kernel"; else bad "too old, need >= 6.15"; fi

echo "2. io_uring enabled"
v=$(sysctl -n kernel.io_uring_disabled 2>/dev/null)
[ -z "$v" ] && info "kernel.io_uring_disabled absent (not gated)" || \
  { [ "$v" = 0 ] && ok "kernel.io_uring_disabled=0" || bad "io_uring disabled ($v)"; }

echo "3. io_uring RECV_ZC opcode (definitive)"
if command -v gcc >/dev/null; then
  d=$(mktemp -d)
  cat > "$d/p.c" <<'EOF'
#define _GNU_SOURCE
#include <stdio.h>
#include <string.h>
#include <linux/io_uring.h>
#include <sys/syscall.h>
#include <unistd.h>
int main(void){
  struct io_uring_params p; memset(&p,0,sizeof p);
  int fd=syscall(__NR_io_uring_setup,8,&p); if(fd<0)return 2;
  unsigned char b[sizeof(struct io_uring_probe)+256*sizeof(struct io_uring_probe_op)];
  memset(b,0,sizeof b);
  if(syscall(__NR_io_uring_register,fd,IORING_REGISTER_PROBE,b,256)<0)return 3;
  struct io_uring_probe*pr=(void*)b;
  if(pr->last_op<58){printf("no\n");return 0;}
  for(int i=0;i<pr->ops_len;i++)
    if(pr->ops[i].op==58){printf(pr->ops[i].flags&IO_URING_OP_SUPPORTED?"yes\n":"no\n");return 0;}
  printf("no\n");return 0;
}
EOF
  if gcc -O2 -o "$d/p" "$d/p.c" 2>/dev/null; then
    r=$("$d/p"); [ "$r" = yes ] && ok "RECV_ZC (op 58) supported" || bad "RECV_ZC not supported"
  else info "probe build failed (old uapi headers?) — rely on kernel version"; fi
  rm -rf "$d"
else info "gcc absent — skipping definitive opcode probe"; fi

echo "4. .NET 10 SDK"
dotnet --list-sdks 2>/dev/null | grep -q '^10\.' && ok "net10 SDK present" || bad "net10 SDK missing"

echo "5. zcrx-capable NIC (need bnxt_en / mlx5 / gve; igc/virtio/iwlwifi/veth do NOT)"
found=0
for n in $(ls /sys/class/net); do
  [ "$n" = lo ] && continue
  drv=$(readlink -f /sys/class/net/$n/device/driver 2>/dev/null | xargs -r basename)
  case "$drv" in
    bnxt_en|mlx5_core|gve) ok "$n -> $drv (zcrx-capable)"; found=1;;
    "" ) info "$n -> virtual, skip";;
    * ) info "$n -> $drv (not zcrx-capable)";;
  esac
  [ -n "$drv" ] && ethtool -g "$n" 2>/dev/null | grep -qi 'tcp.data.split' && \
    ethtool -g "$n" 2>/dev/null | grep -i 'tcp.data.split' | sed 's/^/        /'
done
[ "$found" = 0 ] && bad "no zcrx-capable NIC -> use netdevsim (see test-netdevsim.md)"

echo "6. netdevsim fallback"
modinfo netdevsim >/dev/null 2>&1 && ok "netdevsim available" || bad "netdevsim missing"

echo "7. privileges for ethtool steering"
[ "$(id -u)" -eq 0 ] && ok "root" || info "non-root: ethtool -G/-L/-X/-N need sudo"
