using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ioxide.file;

/// <summary>
/// An immutable snapshot of a directory, keyed by URL path: one open descriptor plus size/mtime/inode
/// per file, opened once and shared across reactors (reads are positional off the ring, so nothing is
/// locked). No bytes are cached and no HTTP is spoken - a caller looks a path up, reads the current
/// file data off the ring, and frames it however it likes. Deploys swap whole snapshots via
/// <see cref="StaticAssets.Reload"/>. One open descriptor per file - mind RLIMIT_NOFILE.
/// </summary>
public sealed class AssetCache : IDisposable
{
    /// <summary>
    /// A pre-opened file: its descriptor plus the size/mtime/inode captured when the snapshot was
    /// built, used to tell whether the file on disk still matches (see <see cref="IsFresh"/>).
    /// </summary>
    public readonly record struct Asset(int Fd, string Path, long Length, long MtimeSec, uint MtimeNsec, ulong Ino);

    private readonly Dictionary<string, Asset> _assets;
    private readonly SafeFileHandle[] _handles;
    private int _disposed;
    private int _refs = 1;   // the "live" reference held by StaticAssets; leases add/drop more

    /// <summary>The absolute root directory the cache was built over.</summary>
    public string RootDir { get; }

    /// <summary>How many files were opened.</summary>
    public int Count => _assets.Count;

    public AssetCache(string rootDir)
    {
        RootDir = Path.GetFullPath(rootDir);

        if (!Directory.Exists(RootDir))
        {
            throw new DirectoryNotFoundException(RootDir);
        }

        _assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var handles = new List<SafeFileHandle>();

        // Open every file under the root, keyed by its URL path relative to the root. The managed
        // handle is held for the cache's lifetime so the raw fd stays valid.
        //
        // Skip symlinks (files and directories): a symlink could resolve outside the root, so not
        // following them blocks traversal/escape. With RecurseSubdirectories, a skipped (symlinked)
        // directory is also not descended into.
        var walk = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };
        foreach (string path in Directory.EnumerateFiles(RootDir, "*", walk))
        {
            SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            handles.Add(handle);

            int fd = (int)handle.DangerousGetHandle();
            string key = "/" + Path.GetRelativePath(RootDir, path).Replace('\\', '/');

            // Freshness baseline: a request later re-statx's this path and reads the cached fd only
            // while size + mtime + inode still match.
            TryStat(path, out long length, out long mtimeSec, out uint mtimeNsec, out ulong ino);

            _assets[key] = new Asset(fd, path, length, mtimeSec, mtimeNsec, ino);
        }

        _handles = handles.ToArray();
    }

    // --- Revalidation (statx) -------------------------------------------------------------------
    // The cached descriptor is read only while the file on disk still matches what was captured (size
    // + mtime + inode), so an in-place edit or an atomic rename is picked up live instead of serving
    // a stale inode. On a mismatch the caller reopens the path (see TryOpenCurrent).

    private const int  AT_FDCWD          = -100;
    private const uint STATX_BASIC_STATS = 0x000007ffU;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern unsafe int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte* buf);

    /// <summary>
    /// statx a path into (size, mtime, inode). False when the file can't be stat'd (e.g. deleted).
    /// Offsets are from <c>struct statx</c> (linux/stat.h): ino @32, size @40, mtime.sec @112,
    /// mtime.nsec @120.
    /// </summary>
    internal static unsafe bool TryStat(string path, out long size, out long mtimeSec, out uint mtimeNsec, out ulong ino)
    {
        byte* buf = stackalloc byte[256];
        if (statx(AT_FDCWD, path, 0, STATX_BASIC_STATS, buf) != 0)
        {
            size = 0; mtimeSec = 0; mtimeNsec = 0; ino = 0;
            return false;
        }

        ino       = *(ulong*)(buf + 32);
        size      = (long)*(ulong*)(buf + 40);
        mtimeSec  = *(long*)(buf + 112);
        mtimeNsec = *(uint*)(buf + 120);
        return true;
    }

    /// <summary>
    /// True when <paramref name="asset"/> still matches the file on disk (size + mtime + inode).
    /// <paramref name="exists"/> is false when the file is gone; <paramref name="currentSize"/> is the
    /// live size, used to frame a response when serving a changed file.
    /// </summary>
    public static bool IsFresh(in Asset asset, out bool exists, out long currentSize)
    {
        exists = TryStat(asset.Path, out currentSize, out long ms, out uint mn, out ulong ino);
        return exists
            && currentSize == asset.Length
            && ms == asset.MtimeSec
            && mn == asset.MtimeNsec
            && ino == asset.Ino;
    }

    /// <summary>
    /// The descriptor and length to read for the CURRENT contents of <paramref name="asset"/>, so a
    /// caller gets live data without tracking changes itself: the cached fd when the file is
    /// unchanged, or a freshly opened one when it changed on disk (an edit or atomic rename). When
    /// <paramref name="reopened"/> is non-null the returned fd is fresh and the caller must dispose it
    /// after reading; when it's null the fd is the snapshot's shared descriptor (do not close it).
    /// Returns false when the file is gone.
    /// </summary>
    public static bool TryOpenCurrent(in Asset asset, out int fd, out long length, out SafeFileHandle? reopened)
    {
        if (IsFresh(asset, out bool exists, out long currentSize))
        {
            fd = asset.Fd;
            length = asset.Length;
            reopened = null;
            return true;
        }

        if (!exists)
        {
            fd = 0;
            length = 0;
            reopened = null;
            return false;
        }

        try
        {
            reopened = File.OpenHandle(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            fd = 0;
            length = 0;
            reopened = null;
            return false;
        }

        fd = (int)reopened.DangerousGetHandle();
        length = currentSize;
        return true;
    }

    /// <summary>Look up a pre-opened asset by URL path; false if there's no such file.</summary>
    public bool TryGet(string urlPath, out Asset asset) => _assets.TryGetValue(urlPath, out asset);

    /// <summary>
    /// Span-based lookup for the hot path - resolves the request target straight from the recv
    /// buffer, with no per-request string allocation.
    /// </summary>
    public bool TryGet(ReadOnlySpan<byte> urlPath, out Asset asset)
    {
        if (urlPath.Length is 0 or > 1024)
        {
            asset = default;
            return false;
        }

        Span<char> chars = stackalloc char[urlPath.Length];
        if (Ascii.ToUtf16(urlPath, chars, out int written) != OperationStatus.Done)
        {
            asset = default;
            return false;   // keys are ASCII URL paths; anything else can't match
        }

        return _assets.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(chars[..written], out asset);
    }

    /// <summary>
    /// Take a reference so this snapshot can't be freed while it's in use; false if it's already
    /// being torn down (the caller should re-read the live snapshot). Pair with <see cref="Release"/>.
    /// </summary>
    internal bool TryAddRef()
    {
        int r;
        do
        {
            r = Volatile.Read(ref _refs);
            if (r == 0)
            {
                return false;   // already releasing - don't resurrect
            }
        }
        while (Interlocked.CompareExchange(ref _refs, r + 1, r) != r);
        return true;
    }

    /// <summary>Drop a reference; the snapshot is freed when the last one goes.</summary>
    internal void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            DisposeCore();
        }
    }

    /// <summary>Drops the "live" reference; the snapshot frees once all outstanding leases release too.</summary>
    public void Dispose() => Release();

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (SafeFileHandle handle in _handles)
        {
            handle.Dispose();
        }

        _assets.Clear();
    }
}
