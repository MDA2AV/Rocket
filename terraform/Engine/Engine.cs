using terraform.Ring;
using static terraform.Ring.Constants;

namespace terraform.Engine;

public sealed unsafe partial class Engine
{
    private static IoUring CreateRing(uint flags, int sqThreadCpu, uint sqThreadIdleMs, uint ringEntries)
    {
        return IoUring.Create(ringEntries, flags, sqThreadCpu, sqThreadIdleMs);
    }

    private static IoUringSqe* SqeGet(IoUring ring)
    {
        IoUringSqe* sqe = ring.GetSqe();
        if (sqe == null)
        {
            ring.Submit();
            sqe = ring.GetSqe();
        }
        return sqe;
    }

    private static void ArmRecvMultishot(IoUring ring, int fd, ushort bgid)
    {
        IoUringSqe* sqe = SqeGet(ring);
        Sqe.PrepRecvMultishotSelect(sqe, fd, bgid, 0);
        sqe->user_data = PackUd(UdKind.Recv, fd);
    }
}
