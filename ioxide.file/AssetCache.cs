using Microsoft.Win32.SafeHandles;

namespace ioxide.file;

/// <summary>
/// A directory of static assets, opened once. Every file under the root is opened read-only at
/// construction and its descriptor is kept for the cache's lifetime, keyed by URL path (e.g.
/// "/css/app.css"). Reads are positional and the descriptors never change, so a single asset can
/// serve many connections — and many reactors — at the same time.
///
/// No length is stored on purpose: the handler derives Content-Length from the bytes it actually
/// reads, so a file edited on disk can never serve a stale length. One descriptor stays open per
/// file, so mind RLIMIT_NOFILE for very large trees.
/// </summary>
public sealed class AssetCache : IDisposable
{
    /// <summary>A pre-opened asset: its raw descriptor and absolute path.</summary>
    public readonly record struct Asset(int Fd, string Path);

    private readonly Dictionary<string, Asset> _assets;
    private readonly SafeFileHandle[] _handles;
    private int _disposed;

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
        foreach (string path in Directory.EnumerateFiles(RootDir, "*", SearchOption.AllDirectories))
        {
            SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            handles.Add(handle);

            int fd = (int)handle.DangerousGetHandle();
            string key = "/" + Path.GetRelativePath(RootDir, path).Replace('\\', '/');

            _assets[key] = new Asset(fd, path);
        }

        _handles = handles.ToArray();
    }

    /// <summary>Look up a pre-opened asset by URL path; false if there's no such file.</summary>
    public bool TryGet(string urlPath, out Asset asset) => _assets.TryGetValue(urlPath, out asset);

    public void Dispose()
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
