using System.Runtime.InteropServices;

namespace terraform.Ring;

internal static unsafe class Syscalls
{
    [DllImport("libc", EntryPoint = "syscall")]
    private static extern long syscall3(long nr, uint entries, IoUringParams* p);

    [DllImport("libc", EntryPoint = "syscall")]
    private static extern long syscall6(long nr, uint fd, uint to_submit, uint min_complete, uint flags, void* arg, nuint argsz);

    [DllImport("libc", EntryPoint = "syscall")]
    private static extern long syscall4(long nr, uint fd, uint opcode, void* arg, uint nr_args);

    /// <summary>
    /// io_uring_setup(entries, params) → ring fd or -errno.
    /// </summary>
    internal static int io_uring_setup(uint entries, IoUringParams* p)
    {
        long ret = syscall3(Constants.SYS_io_uring_setup, entries, p);
        return (int)ret;
    }

    /// <summary>
    /// io_uring_enter(fd, to_submit, min_complete, flags, arg, argsz) → submitted or -errno.
    /// </summary>
    internal static int io_uring_enter(int fd, uint toSubmit, uint minComplete, uint flags, void* arg, nuint argsz)
    {
        long ret = syscall6(Constants.SYS_io_uring_enter, (uint)fd, toSubmit, minComplete, flags, arg, argsz);
        return (int)ret;
    }

    /// <summary>
    /// io_uring_register(fd, opcode, arg, nr_args) → 0 or -errno.
    /// </summary>
    internal static int io_uring_register(int fd, uint opcode, void* arg, uint nrArgs)
    {
        long ret = syscall4(Constants.SYS_io_uring_register, (uint)fd, opcode, arg, nrArgs);
        return (int)ret;
    }

    [DllImport("libc")]
    internal static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc")]
    internal static extern int munmap(void* addr, nuint length);

    [DllImport("libc")]
    internal static extern int close(int fd);
}
