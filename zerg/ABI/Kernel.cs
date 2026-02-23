using System.Runtime.InteropServices;

namespace zerg.ABI;

public static partial class ABI{
    // ------------------------------------------------------------------------------------
    //  POSIX TIME STRUCT
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Kernel-compatible timespec (seconds + nanoseconds).
    /// <para>
    /// Used by io_uring for relative/absolute timeouts. Matches the Linux
    /// <c>struct __kernel_timespec</c> layout: two 64-bit signed integers.
    /// </para>
    /// <remarks>
    /// Keep this strictly sequential and 16 bytes in size:
    /// <list type="bullet">
    /// <item><description><c>tv_sec</c>   at offset 0 (8 bytes)</description></item>
    /// <item><description><c>tv_nsec</c>  at offset 8 (8 bytes)</description></item>
    /// </list>
    /// </remarks>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct __kernel_timespec {
        public long tv_sec;   // seconds (signed 64-bit)
        public long tv_nsec;  // nanoseconds (signed 64-bit)
    }
}