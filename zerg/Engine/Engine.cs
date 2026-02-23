using static zerg.ABI.ABI;

namespace zerg.Engine;

public sealed unsafe partial class Engine 
{
    private static io_uring* CreateRing(uint flags, int sqThreadCpu, uint sqThreadIdleMs, out int err, uint ringEntries) 
    {
        if(flags == 0)
            return shim_create_ring(ringEntries, out err);
        return shim_create_ring_ex(ringEntries, flags, sqThreadCpu, sqThreadIdleMs, out err);
    }

    // TODO: This seems to be causing segfault when sqe is null
    private static io_uring_sqe* SqeGet(io_uring* pring) 
    {
        io_uring_sqe* sqe = shim_get_sqe(pring);
        if (sqe == null) {
            Console.WriteLine("S4");
            shim_submit(pring); 
            sqe = shim_get_sqe(pring); 
        }
        return sqe;
    }

    private static void ArmRecvMultishot(io_uring* pring, int fd, uint bgid) 
    {
        io_uring_sqe* sqe = SqeGet(pring);
        shim_prep_recv_multishot_select(sqe, fd, bgid, 0);
        shim_sqe_set_data64(sqe, PackUd(UdKind.Recv, fd));
    }
}