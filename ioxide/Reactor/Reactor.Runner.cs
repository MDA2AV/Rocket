using System.Runtime.InteropServices;
using static ioxide.Native;

namespace ioxide;

public sealed unsafe partial class Reactor
{
    // Off-reactor handoff queues + eventfd wake. Reactor-thread callers take the
    // direct fast path instead (no queue, no syscall).
    private int _wakeFd;
    private int _reactorThreadId;
    
    private int[] _listenFds = [];
    private ushort[] _listenPorts = [];
    private readonly ServerConfig _config;
    
    // Loop Mode
    private readonly bool _incremental;
    
    public void Run()
    {
        _reactorThreadId = Environment.CurrentManagedThreadId;

        // Awaits from reactor code (timers, HttpClient, Task.Run results) resume here instead of
        // the thread pool. Thread-lifetime; nothing to uninstall.
        SynchronizationContext.SetSynchronizationContext(new ReactorSynchronizationContext(this));

        _ring = Ring.Create(_ringEntries);

        // One SO_REUSEPORT listener per port; accepts route by listener fd.
        _listenFds = new int[1 + _config.ExtraPorts.Length];
        _listenPorts = new ushort[_listenFds.Length];
        _listenPorts[0] = _port;
        for (int i = 0; i < _config.ExtraPorts.Length; i++)
        {
            _listenPorts[i + 1] = _config.ExtraPorts[i];
        }
        for (int i = 0; i < _listenFds.Length; i++)
        {
            _listenFds[i] = OpenReusePortListener(_listenPorts[i], _config.ListenBacklog, _config.DualStack);
        }

        if (_incremental)
        {
            InitIncremental();
        }
        else
        {
            InitSharedRingBuffer();
        }

        _wakeFd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
        if (_wakeFd < 0)
        {
            throw new InvalidOperationException("eventfd failed");
        }

        // Ring-native clients must be opened on this thread; async opens complete
        // once the loop starts.
        OnStart?.Invoke(this);

        Console.WriteLine($"[r{_id}] listening on 0.0.0.0:{string.Join(",", _listenPorts)} (incremental={_incremental})");
        foreach (int listenFd in _listenFds)
        {
            SubmitAcceptMultishot(listenFd);
        }
        ArmWakePoll();

        _timerTs = (Native.__kernel_timespec*)NativeMemory.Alloc((nuint)sizeof(Native.__kernel_timespec));
        _timerTs->tv_sec  = 0;
        _timerTs->tv_nsec = TimerIntervalNs;
        ArmTimer();

        if (_incremental)
        {
            LoopIncremental();
        }
        else
        {
            LoopSharedRing();
        }

        foreach (int listenFd in _listenFds)
        {
            close(listenFd);
        }

        // Close any still-open accepted sockets (the connection table is indexed by fd) so they don't
        // leak when a host is disposed and recreated many times in one process - e.g. across a test run.
        for (int fd = 0; fd < _connections.Length; fd++)
        {
            if (_connections[fd] != null)
            {
                close(fd);
                _connections[fd] = null;
            }
        }

        close(_wakeFd);
        if (_timerTs != null)
        {
            NativeMemory.Free(_timerTs);
            _timerTs = null;
        }
        _ring.Dispose();

        // Shared provided-buffer ring (incremental mode allocates per connection instead). Freed after
        // the ring fd is closed, so the kernel has dropped its references to the slab.
        if (_bufRing != null)
        {
            NativeMemory.AlignedFree(_bufRing);
            _bufRing = null;
        }
        if (_bufSlab != null)
        {
            NativeMemory.AlignedFree(_bufSlab);
            _bufSlab = null;
        }
        
    }
    
    // Set cross-thread by Stop(); the loops check it at the top of each iteration and exit, after which
    // Run() tears the ring down on this (the reactor) thread - mandatory for a single-issuer ring.
    private volatile bool _stopRequested;
    
    /// <summary>
    /// Requests the reactor to stop. Safe to call from any thread. The loop finishes its current
    /// iteration and exits, then <see cref="Run"/> closes the listeners and wake fd and disposes the
    /// io_uring ring on the reactor thread (a single-issuer / DEFER_TASKRUN ring must be torn down on the
    /// thread that owns it). Join the reactor thread after calling this to await teardown.
    /// </summary>
    public void Stop()
    {
        _stopRequested = true;

        // Wake a loop parked in io_uring_enter so it observes the flag promptly. Writing the eventfd is
        // the only ring-adjacent action safe off the reactor thread. The guard covers Stop() racing ahead
        // of Run() creating _wakeFd - the loop still sees the flag on its first iteration.
        if (_wakeFd > 0)
        {
            WakeFdWrite();
        }
    }
}