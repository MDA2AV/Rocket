namespace ioxide.file;

/// <summary>
/// A reloadable holder around an <see cref="AssetCache"/>. The cache is an immutable snapshot of a
/// directory; <see cref="Reload"/> builds a fresh snapshot, swaps it in atomically, and closes the
/// old descriptors after a grace period.
///
/// Handlers read the live snapshot through <see cref="TryGet"/> (a volatile read), so a reactor
/// always sees the whole old snapshot or the whole new one — never a mix — and Content-Length always
/// matches the body shipped with it. Call <see cref="Reload"/> after a deploy, e.g. from a SIGHUP
/// handler.
/// </summary>
public sealed class StaticAssets : IDisposable
{
    // io_uring holds a reference to a file for any read already submitted, so this grace only has to
    // outlast the gap between a handler resolving an asset and submitting its read. Deploys are rare,
    // so a generous window costs nothing.
    private static readonly TimeSpan ReloadGrace = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly object _reloadLock = new();

    private AssetCache _cache;   // swapped atomically by Reload(), read with Volatile

    public StaticAssets(string rootDir)
    {
        _root = rootDir;
        _cache = new AssetCache(rootDir);
    }

    public int Count => Volatile.Read(ref _cache).Count;

    public string RootDir => Volatile.Read(ref _cache).RootDir;

    /// <summary>Look up a pre-opened asset by URL path in the live snapshot.</summary>
    public bool TryGet(string urlPath, out AssetCache.Asset asset)
        => Volatile.Read(ref _cache).TryGet(urlPath, out asset);

    /// <summary>
    /// Rebuild the snapshot from disk and swap it in atomically, then dispose the old one after a
    /// grace period. If the rebuild fails, the current snapshot is kept. Thread-safe.
    /// </summary>
    public void Reload()
    {
        lock (_reloadLock)
        {
            AssetCache fresh;
            try
            {
                fresh = new AssetCache(_root);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[ioxide] asset reload failed, keeping current snapshot: {e.Message}");
                return;
            }

            AssetCache old = Interlocked.Exchange(ref _cache, fresh);

            _ = Task.Delay(ReloadGrace).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
        }
    }

    public void Dispose() => Volatile.Read(ref _cache).Dispose();
}
