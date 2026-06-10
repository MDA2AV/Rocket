using System.Runtime.InteropServices;

namespace ioxide.file;

/// <summary>
/// libc calls for opening a file and learning its size. The reads themselves go through the ring,
/// not through these.
/// </summary>
internal static unsafe class Native
{
    public const int O_RDONLY = 0;

    private const int SEEK_SET = 0;
    private const int SEEK_END = 2;

    [DllImport("libc", SetLastError = true)]
    public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc")]
    public static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    public static extern long lseek(int fd, long offset, int whence);

    /// <summary>
    /// Get a file's size by seeking to the end and back. Simpler and more portable here than
    /// declaring the platform's <c>stat</c> struct.
    /// </summary>
    public static long FileLength(int fd)
    {
        long end = lseek(fd, 0, SEEK_END);

        lseek(fd, 0, SEEK_SET);

        return end;
    }
}
