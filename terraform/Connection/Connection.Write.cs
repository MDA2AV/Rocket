using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using terraform.Utils.UnmanagedMemoryManager;

namespace terraform;

public unsafe partial class Connection
{
    private ManualResetValueTaskSourceCore<bool> _flushSignal;
    private int _flushArmed;
    private int _flushInProgress;

    internal bool IsFlushInProgress => Volatile.Read(ref _flushInProgress) != 0;

    private readonly int _writeSlabSize;
    private readonly UnmanagedMemoryManager _manager;

    public byte* WriteBuffer { get; }
    internal int WriteHead { get; set; }
    public int WriteTail { get; private set; }
    internal int WriteInFlight { get; set; }
    internal int SendInflight;

    public Connection(int writeSlabSize = 1024 * 16)
    {
        _writeSlabSize = writeSlabSize;
        WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)(writeSlabSize), 64);
        WriteHead = 0; WriteTail = 0;

        _manager = new UnmanagedMemoryManager(WriteBuffer, writeSlabSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetWriteBuffer()
    {
        WriteHead = 0;
        WriteTail = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CompleteFlush()
    {
        Volatile.Write(ref _flushInProgress, 0);
        Volatile.Write(ref _flushArmed, 0);
        _flushSignal.SetResult(true);
    }

    void IValueTaskSource.GetResult(short token)
        => _flushSignal.GetResult(_flushSignal.Version);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
        => _flushSignal.GetStatus(_flushSignal.Version);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _flushSignal.OnCompleted(continuation, state, _flushSignal.Version, flags);
}
