using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace Rocket;

[SkipLocalsInit]
public sealed unsafe class Connection : IValueTaskSource<bool>
{
    public bool HasBuffer;
    public ushort BufferId;

    public int Fd;
    public int ReactorId;

    // In buffer (points into reactor's buffer-ring slab)
    public byte* InPtr;
    public int InLength;

    // Out buffer
    public nuint OutHead, OutTail;
    public byte* OutPtr;

    // Reusable completion primitive (no Task/TCS allocations per read)
    private ManualResetValueTaskSourceCore<bool> _readSignal;

    // Debug guard: enforces "one outstanding ReadAsync at a time"
    private bool _readArmed;

    public Connection(int fd) => Fd = fd;

    public Connection() { }

    /// <summary>
    /// Await until the reactor signals that new bytes are available in InPtr/InLength.
    /// One outstanding await is supported at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<bool> ReadAsync() {
        if (_readArmed) throw new InvalidOperationException("ReadAsync() already armed. Call ResetRead() after consuming the buffer.");
        _readArmed = true;
        return new ValueTask<bool>(this, _readSignal.Version);
    }

    /// <summary>
    /// Called by the reactor thread when it has produced readable bytes for this connection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SignalReadReady() { _readSignal.SetResult(true); }

    /// <summary>
    /// Called by the consumer after it finishes using InPtr/InLength and wants to await the next read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetRead() {
        _readArmed = false;

        // Prepare for next await cycle
        _readSignal.Reset();

        // Clear "current read" metadata (optional but helps avoid misuse)
        //InPtr = null;
        //InLength = 0;
        //HasBuffer = false;
        //BufferId = 0;
    }

    public void Clear() {
        // Reset send state
        OutPtr = null;
        OutHead = 0;
        OutTail = 0;

        // Reset read state so pooled connections don't remain signaled
        _readArmed = false;
        _readSignal.Reset();

        InPtr = null;
        InLength = 0;
        HasBuffer = false;
        BufferId = 0;
    }

    public Connection SetFd(int fd) { Fd = fd; return this; }

    public Connection SetReactorId(int reactorId) { ReactorId = reactorId; return this; }
    
    // IValueTaskSource<bool> plumbing
    bool IValueTaskSource<bool>.GetResult(short token) => _readSignal.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _readSignal.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _readSignal.OnCompleted(continuation, state, token, flags);
}