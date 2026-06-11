using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ioxide.file;

/// <summary>
/// An immutable snapshot of a static-asset directory, keyed by URL path. Small files
/// (≤ maxCachedFileBytes) get their entire HTTP response precomputed in native memory - the hot
/// path serves them with no file I/O, no header formatting, no allocation. Large files keep a
/// descriptor and are read off the ring. Deploys swap whole snapshots via
/// <see cref="StaticAssets.Reload"/>. One open descriptor per file - mind RLIMIT_NOFILE.
/// </summary>
public sealed class AssetCache : IDisposable
{
    public const int DefaultMaxCachedFileBytes = 256 * 1024;

    /// <summary>
    /// A pre-opened asset. <see cref="Response"/> is non-zero when the complete HTTP response is
    /// precomputed in native memory (<see cref="ResponseLength"/> bytes - send it as-is);
    /// otherwise read <see cref="Fd"/> positionally off the ring and build the response.
    /// </summary>
    public readonly record struct Asset(int Fd, string Path, long Length, nint Response, int ResponseLength);

    private readonly Dictionary<string, Asset> _assets;
    private readonly SafeFileHandle[] _handles;
    private readonly nint[] _responses;
    private int _disposed;

    /// <summary>The absolute root directory the cache was built over.</summary>
    public string RootDir { get; }

    /// <summary>How many files were opened.</summary>
    public int Count => _assets.Count;

    public AssetCache(string rootDir, int maxCachedFileBytes = DefaultMaxCachedFileBytes)
    {
        RootDir = Path.GetFullPath(rootDir);

        if (!Directory.Exists(RootDir))
        {
            throw new DirectoryNotFoundException(RootDir);
        }

        _assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var handles = new List<SafeFileHandle>();
        var responses = new List<nint>();

        // Open every file under the root, keyed by its URL path relative to the root. The managed
        // handle is held for the cache's lifetime so the raw fd stays valid.
        foreach (string path in Directory.EnumerateFiles(RootDir, "*", SearchOption.AllDirectories))
        {
            SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            handles.Add(handle);

            long length = RandomAccess.GetLength(handle);

            nint response = 0;
            int responseLength = 0;
            if (length <= maxCachedFileBytes)
            {
                (response, responseLength) = BuildResponse(handle, (int)length, path);
                responses.Add(response);
            }

            int fd = (int)handle.DangerousGetHandle();
            string key = "/" + Path.GetRelativePath(RootDir, path).Replace('\\', '/');

            _assets[key] = new Asset(fd, path, length, response, responseLength);
        }

        _handles = handles.ToArray();
        _responses = responses.ToArray();
    }

    // Bake "HTTP/1.1 200 OK ..." + body into one contiguous native block. The body is read first
    // so a file truncated under us still yields a consistent Content-Length.
    private static unsafe (nint Response, int Length) BuildResponse(SafeFileHandle handle, int bodyLength, string path)
    {
        nint scratch = (nint)NativeMemory.Alloc((nuint)Math.Max(bodyLength, 1));
        int read = 0;
        while (read < bodyLength)
        {
            int n = RandomAccess.Read(handle, new Span<byte>((void*)(scratch + read), bodyLength - read), read);
            if (n <= 0)
            {
                break;
            }
            read += n;
        }

        Span<byte> header = stackalloc byte[256];
        int h = WriteResponseHeader(header, path, read);

        nint response = (nint)NativeMemory.Alloc((nuint)(h + read));
        header[..h].CopyTo(new Span<byte>((void*)response, h));
        Buffer.MemoryCopy((void*)scratch, (void*)(response + h), read, read);
        NativeMemory.Free((void*)scratch);

        return (response, h + read);
    }

    /// <summary>
    /// Write the 200 response header for <paramref name="path"/> into
    /// <paramref name="destination"/> (≥256 bytes); returns the bytes written. The one place
    /// asset headers are formatted - snapshot baking and ring-read serving both use it.
    /// </summary>
    public static int WriteResponseHeader(Span<byte> destination, string path, int bodyLength)
    {
        int h = 0;
        h += Copy(destination[h..], "HTTP/1.1 200 OK\r\nContent-Type: "u8);
        h += Copy(destination[h..], MimeFor(path));
        h += Copy(destination[h..], "\r\nContent-Length: "u8);
        Utf8Formatter.TryFormat(bodyLength, destination[h..], out int digits);
        h += digits;
        h += Copy(destination[h..], "\r\n\r\n"u8);
        return h;
    }

    private static int Copy(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    private static ReadOnlySpan<byte> MimeFor(string path) => System.IO.Path.GetExtension(path) switch
    {
        ".html"  => "text/html"u8,
        ".css"   => "text/css"u8,
        ".js"    => "application/javascript"u8,
        ".json"  => "application/json"u8,
        ".svg"   => "image/svg+xml"u8,
        ".png"   => "image/png"u8,
        ".webp"  => "image/webp"u8,
        ".woff2" => "font/woff2"u8,
        ".txt"   => "text/plain"u8,
        ".bin"   => "application/octet-stream"u8,
        _        => "application/octet-stream"u8
    };

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

    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (SafeFileHandle handle in _handles)
        {
            handle.Dispose();
        }

        foreach (nint response in _responses)
        {
            NativeMemory.Free((void*)response);
        }

        _assets.Clear();
    }
}
