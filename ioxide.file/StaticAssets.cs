namespace ioxide.file;

/// <summary>
/// A reloadable holder around an <see cref="AssetCache"/>: <see cref="Reload"/> builds a fresh
/// snapshot, swaps it atomically, and disposes the old one after a grace. Handlers always see a
/// whole snapshot, never a mix. Call after a deploy (e.g. SIGHUP).
/// </summary>
public sealed class StaticAssets : IDisposable
{
    // Must outlast a handler's use of the old snapshot: the gap to a submitted read,
    // or a whole chunked send of the largest baked response.
    private static readonly TimeSpan ReloadGrace = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly int _maxCachedFileBytes;
    private readonly object _reloadLock = new();

    private AssetCache _cache;   // swapped atomically by Reload(), read with Volatile

    public StaticAssets(string rootDir, int maxCachedFileBytes = AssetCache.DefaultMaxCachedFileBytes)
    {
        _root = rootDir;
        _maxCachedFileBytes = maxCachedFileBytes;
        _cache = new AssetCache(rootDir, maxCachedFileBytes);
    }

    public int Count => Volatile.Read(ref _cache).Count;

    public string RootDir => Volatile.Read(ref _cache).RootDir;

    /// <summary>Look up a pre-opened asset by URL path in the live snapshot.</summary>
    public bool TryGet(string urlPath, out AssetCache.Asset asset)
        => Volatile.Read(ref _cache).TryGet(urlPath, out asset);

    /// <summary>Hot-path lookup straight from request bytes - no per-request string.</summary>
    public bool TryGet(ReadOnlySpan<byte> urlPath, out AssetCache.Asset asset)
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
                fresh = new AssetCache(_root, _maxCachedFileBytes);
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
