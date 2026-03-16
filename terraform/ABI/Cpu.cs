using System.Runtime.InteropServices;

namespace terraform.ABI;

internal static class Affinity
{
    private const int ENOSYS     = 38;
    private const int EINVAL     = 22;
    private const int EPERM      = 1;
    private const long SYS_gettid = 186;

    [DllImport("libc")] private static extern long syscall(long n);
    [DllImport("libc")] private static extern int sched_setaffinity(int pid, nuint cpusetsize, byte[] mask);

    public static void PinCurrentThreadToCpu(int cpu)
    {
        int tid   = (int)syscall(SYS_gettid);
        int bytes = (Environment.ProcessorCount + 7) / 8;
        var mask  = new byte[Math.Max(bytes, 8)];
        mask[cpu / 8] |= (byte)(1 << (cpu % 8));
        _ = sched_setaffinity(tid, (nuint)mask.Length, mask);
    }

    public static void ImprovedPinCurrentThreadToCpu(int cpu)
    {
        int cpuCount = Environment.ProcessorCount;
        if ((uint)cpu >= (uint)cpuCount)
            throw new ArgumentOutOfRangeException(nameof(cpu), cpu, $"CPU must be in [0, {cpuCount - 1}]");

        long tidL = syscall(SYS_gettid);
        if (tidL <= 0)
        {
            int errno = Marshal.GetLastWin32Error();
            if (errno == ENOSYS)
                throw new PlatformNotSupportedException("SYS_gettid is not supported on this platform/arch.");
            throw new InvalidOperationException($"syscall(SYS_gettid) failed. errno={errno}");
        }
        int tid = checked((int)tidL);

        int bytesNeeded = (cpuCount + 7) / 8;
        int maskLen = Math.Max(bytesNeeded, 8);
        var mask = new byte[maskLen];
        mask[cpu >> 3] = (byte)(1 << (cpu & 7));

        int rc = sched_setaffinity(tid, (nuint)mask.Length, mask);
        if (rc != 0)
        {
            int errno = Marshal.GetLastWin32Error();
            string hint = errno switch
            {
                EINVAL => "EINVAL: invalid CPU mask/size (cpu out of range, or cpuset size mismatch).",
                EPERM  => "EPERM: insufficient permissions (container/cgroup restrictions or missing caps).",
                _      => "See errno for details."
            };
            throw new InvalidOperationException(
                $"sched_setaffinity(tid={tid}, cpu={cpu}) failed. errno={errno}. {hint}");
        }
    }
}
