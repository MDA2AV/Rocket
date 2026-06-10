using System.Runtime.InteropServices;

namespace Rhythm;

/// <summary>CPU affinity: discover the cgroup-allowed CPUs and pin a thread.</summary>
internal static unsafe class Affinity
{
    private const int MaskBytes = 128; // 1024 CPUs

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, nuint cpusetsize, byte* mask);
    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, nuint cpusetsize, byte* mask);

    /// Fill <paramref name="dst"/> with the CPU ids this process is allowed on
    /// (respects the container cpuset); returns the count.
    public static int Allowed(Span<int> dst)
    {
        byte* mask = stackalloc byte[MaskBytes];
        int n = 0;
        if (sched_getaffinity(0, MaskBytes, mask) == 0)
        {
            for (int b = 0; b < MaskBytes && n < dst.Length; b++)
                for (int bit = 0; bit < 8 && n < dst.Length; bit++)
                    if ((mask[b] & (1 << bit)) != 0) dst[n++] = b * 8 + bit;
        }
        return n;
    }

    /// Pin the calling thread to a single CPU (pid 0 = current thread on Linux).
    public static void Pin(int cpu)
    {
        if (cpu < 0) return;
        byte* m = stackalloc byte[MaskBytes];
        for (int i = 0; i < MaskBytes; i++) m[i] = 0;
        m[cpu / 8] = (byte)(1 << (cpu % 8));
        sched_setaffinity(0, MaskBytes, m);
    }
}
