using System.Runtime.InteropServices;
using static Magpie.Native;

namespace Magpie;

/// <summary>
/// io_uring-backed file reader. Each read borrows a ring from a pool, submits an
/// <c>IORING_OP_READ</c>, and completes synchronously — inline on a page-cache hit,
/// blocking on a miss (the borrowed ring waits for io_uring's io-wq worker to finish
/// the disk read). The result is handed back as a completed <see cref="ValueTask{T}"/>
/// so it composes with async callers; there's no reactor, so nothing completes it later.
/// Rings are not thread-affine, only used one-at-a-time, so any thread may call.
/// </summary>
public sealed unsafe class MagpieReader : IDisposable
{
    private readonly RingPool _pool;

    /// <param name="rings">Pool size = max concurrent in-flight reads. 0 ⇒ ProcessorCount.</param>
    /// <param name="depth">SQ/CQ depth per ring. One op in flight at a time, so small is fine.</param>
    public MagpieReader(int rings = 0, uint depth = 32) => _pool = new RingPool(rings, depth);

    /// <summary>Open a file read-only and return its fd (caller closes via <see cref="Close"/>).</summary>
    public static int OpenRead(string path)
    {
        int fd = open(path, O_RDONLY, 0);
        if (fd < 0) throw new IOException($"open('{path}') failed: errno={Marshal.GetLastPInvokeError()}");
        return fd;
    }

    public static void Close(int fd) => close(fd);

    /// <summary>File size in bytes (lseek to SEEK_END). io_uring reads use explicit
    /// offsets, so moving the fd position here is harmless.</summary>
    public static long Size(int fd) => lseek(fd, 0, SEEK_END);

    /// <summary>Read into <paramref name="buffer"/> from <paramref name="offset"/>. Completes synchronously.</summary>
    public ValueTask<int> ReadAsync(int fd, Memory<byte> buffer, long offset)
        => new(Read(fd, buffer.Span, offset));

    /// <summary>Synchronous read: bytes read (0 = EOF). Throws on a negative errno.</summary>
    public int Read(int fd, Span<byte> buffer, long offset)
    {
        if (buffer.IsEmpty) return 0;

        Ring ring = _pool.Rent();
        try
        {
            fixed (byte* p = buffer)
            {
                int res = ring.SubmitRead(fd, p, (uint)buffer.Length, offset);
                if (res < 0) throw new IOException($"io_uring read failed: errno={-res}");
                return res;
            }
        }
        finally { _pool.Return(ring); }
    }

    public void Dispose() => _pool.Dispose();
}
