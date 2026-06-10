using System.Runtime.InteropServices;
using ioxide;
using ioxide.file;

namespace Playground;

/// <summary>
/// Per-reactor state for the file handler. The <see cref="AssetCache"/> is shared across reactors
/// (its descriptors are stable and read positionally); each reactor gets its own
/// <see cref="RingReader"/> and read buffer, so it serves assets on its own ring.
///
/// One read in flight at a time per reactor — concurrent requests on the same reactor serialize on
/// the reader. A per-reactor pool of readers is the fix, as it is for the Postgres client.
/// </summary>
internal sealed class FileEndpoint
{
    private const int BufferBytes = 1 << 20;   // 1 MB; assets larger than this are truncated in this demo

    public required StaticAssets Assets { get; init; }
    public required RingReader Reader { get; init; }
    public required nint Buffer { get; init; }
    public required int BufferSize { get; init; }

    public static unsafe FileEndpoint Open(Reactor reactor, StaticAssets assets)
    {
        return new FileEndpoint
        {
            Assets = assets,
            Reader = new RingReader(reactor),
            Buffer = (nint)NativeMemory.Alloc(BufferBytes),
            BufferSize = BufferBytes
        };
    }
}
