using System.Runtime.InteropServices;

namespace zerg.ABI;

public static partial class ABI {
    // ------------------------------------------------------------------------------------
    //  CPU AFFINITY PINNING
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Helpers to pin the current Linux thread to a specific CPU core.
    /// <para>
    /// Useful for deterministic benchmarking or to reduce scheduler migrations.
    /// Non-fatal if pinning fails (the call is best-effort).
    /// </para>
    /// </summary>
    internal static class Affinity {
        private const int ENOSYS     = 38;
        private const int EINVAL     = 22;
        private const int EPERM      = 1;
        private const long SYS_gettid = 186; // Linux gettid syscall number (x86_64)
        [DllImport("libc")] private static extern long syscall(long n);
        /// <summary>
        /// Sets the CPU affinity mask for a given thread id.
        /// </summary>
        [DllImport("libc")] private static extern int sched_setaffinity(int pid, nuint cpusetsize, byte[] mask);
        /// <summary>
        /// Pins the calling thread to <paramref name="cpu"/> (zero-based).
        /// <para>
        /// Builds a minimal CPU set and invokes <c>sched_setaffinity</c>. Errors are ignored intentionally.
        /// </para>
        /// </summary>
        public static void PinCurrentThreadToCpu(int cpu) {
            int tid   = (int)syscall(SYS_gettid);
            int bytes = (Environment.ProcessorCount + 7) / 8;
            var mask  = new byte[Math.Max(bytes, 8)]; // ensure minimal size for safety
            mask[cpu / 8] |= (byte)(1 << (cpu % 8));
            _ = sched_setaffinity(tid, (nuint)mask.Length, mask);
        }
        /// <summary>
        /// Pins the *current Linux thread* (kernel TID) to a single logical CPU.
        /// </summary>
        public static void ImprovedPinCurrentThreadToCpu(int cpu)
        {
            int cpuCount = Environment.ProcessorCount;
            if ((uint)cpu >= (uint)cpuCount)
                throw new ArgumentOutOfRangeException(nameof(cpu), cpu, $"CPU must be in [0, {cpuCount - 1}]");

            // Get kernel thread id (TID)
            long tidL = syscall(SYS_gettid);
            if (tidL <= 0) {
                int errno = Marshal.GetLastWin32Error();
                if (errno == ENOSYS)
                    throw new PlatformNotSupportedException("SYS_gettid is not supported on this platform/arch.");
                throw new InvalidOperationException($"syscall(SYS_gettid) failed. errno={errno}");
            }
            int tid = checked((int)tidL);
            // Build a CPU bitmask with a single bit set.
            // Linux expects a cpuset bitmask sized in bytes; 1 bit per CPU.
            int bytesNeeded = (cpuCount + 7) / 8;
            // Some libc/kernel combos behave better with at least 8 bytes (64 CPUs) worth of mask.
            int maskLen = Math.Max(bytesNeeded, 8);
            var mask = new byte[maskLen];
            mask[cpu >> 3] = (byte)(1 << (cpu & 7));
            // Apply affinity to this thread.
            int rc = sched_setaffinity(tid, (nuint)mask.Length, mask);
            if (rc != 0) {
                int errno = Marshal.GetLastWin32Error();
                // Helpful diagnostics
                string hint = errno switch {
                    EINVAL => "EINVAL: invalid CPU mask/size (cpu out of range, or cpuset size mismatch).",
                    EPERM  => "EPERM: insufficient permissions (container/cgroup restrictions or missing caps).",
                    _      => "See errno for details."
                };
                throw new InvalidOperationException(
                    $"sched_setaffinity(tid={tid}, cpu={cpu}) failed. errno={errno}. {hint}");
            }
        }
    }
}