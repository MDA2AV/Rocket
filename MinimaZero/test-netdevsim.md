# Testing MinimaZero without zcrx hardware

This box has no zcrx-capable NIC (`igc`/`iwlwifi` can't do tcp-data-split).
The kernel side is fine (6.17, `RECV_ZC` op 58 confirmed supported). Two ways
to functionally exercise the zcrx path:

## A. Ground truth: the in-tree selftest (recommended first)

The kernel's own zcrx selftest is kept in lockstep with the ABI and doubles as
the reference to diff `Native.cs` structs/constants against:

    tools/testing/selftests/drivers/net/hw/iou-zcrx.c   # + iou-zcrx.py harness

Build the kernel selftests for your running kernel and run `iou-zcrx`. If it
passes, the kernel + netdevsim path works and the ABI in `Native.cs` should
match that exact source file. If `iou-zcrx.c` differs from my structs (esp.
`io_uring_zcrx_area_reg` / `io_uring_zcrx_ifq_reg` field order, refill
semantics), trust the selftest and update `Native.cs`/`Reactor.cs`.

## B. Run MinimaZero against a netdevsim device (needs sudo)

netdevsim implements tcp-data-split + flow steering in software, so the whole
zcrx ABI (IFQ register, refill ring, RECV_ZC, CQE32 token decode, recycle)
runs for real. It validates correctness, NOT real-NIC DMA performance.

> netdevsim ethtool support varies by kernel; if any step below errors,
> follow the exact sequence in the selftest's `.py` harness instead — it is
> the maintained recipe.

```bash
sudo modprobe netdevsim
echo "1 1" | sudo tee /sys/bus/netdevsim/new_device     # device id 1, 1 port
DEV=$(ls /sys/bus/netdevsim/devices/netdevsim1/net/)     # e.g. eth0
echo "netdevsim NIC = $DEV"

sudo ip addr add 192.168.99.1/24 dev "$DEV"
sudo ip link set "$DEV" up

sudo ethtool -G "$DEV" tcp-data-split on      # header/data split (mandatory)
sudo ethtool -L "$DEV" combined 2             # >=2 queues
sudo ethtool -X "$DEV" equal 1                # RSS away from the zc queue
sudo ethtool -N "$DEV" flow-type tcp4 dst-port 8080 action 1   # steer -> rxq 1
```

Then point MinimaZero at it — set in `Program.cs`:

```csharp
private const string IfName = "eth0";   // the $DEV printed above
private const uint   IfRxq  = 1;        // the 'action' queue from ethtool -N
```

Run it, then drive traffic to `192.168.99.1:8080` from the peer side of the
netdevsim link (see the selftest harness for the netns/peer wiring — a bare
`curl` from the same host will not traverse the steered queue).

```bash
dotnet run -c Release --project MinimaZero
```

Teardown:

```bash
echo "1" | sudo tee /sys/bus/netdevsim/del_device
```

## Real hardware

On a host with `bnxt_en` (Broadcom) / `mlx5` (NVIDIA-Mellanox) / `gve`
(Google GCP), run `./check-zcrx.sh` — it will report the NIC as zcrx-capable.
Same ethtool steering, real NIC name + steered queue id in `Program.cs`.
