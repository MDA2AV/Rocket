using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using static ioxide.Native;

namespace ioxide;

/// <summary>
/// The reactor as an <see cref="IRingHost"/>. Each client submission allocates a slot holding its
/// completion; the CQE carries the slot in user_data, so routing is O(1) and per-operation -
/// concurrent ops on the same fd never collide and nothing is registered around fd lifetimes.
/// The engine knows nothing about client types.
/// </summary>
public sealed unsafe partial class Reactor : IRingHost
{
    // In-flight client ops: slot → completion. Reactor-thread-only; grows on demand.
    private IRingCompletion?[] _opTargets = new IRingCompletion?[1024];
    private int[] _opFree = null!;
    private int   _opFreeTop;

    // Off-reactor submissions marshal here (single-issuer SQ).
    private readonly record struct RemoteOp(byte Opcode, int Fd, nint Buffer, int Length, long Offset, IRingCompletion Completion);
    private readonly ConcurrentQueue<RemoteOp> _remoteOps = new();

    // Typed per-reactor services (clients opened in OnStart; handlers fetch them).
    private readonly Dictionary<Type, object> _services = new();

    public void AddService<T>(T service) where T : class
    {
        _services[typeof(T)] = service;
    }

    /// <summary>
    /// Get a service registered with <see cref="AddService{T}"/>; throws if absent.
    /// </summary>
    public T GetService<T>() where T : class
    {
        return _services.TryGetValue(typeof(T), out object? service)
            ? (T)service
            : throw new InvalidOperationException(
                $"No {typeof(T).Name} registered on reactor {_id}. Register it from OnStart with AddService.");
    }

    /// <summary>
    /// Runs on the reactor thread before the loop starts - open ring-native clients here.
    /// </summary>
    public Action<Reactor>? OnStart;

    /// <summary>
    /// The per-connection request handler, invoked once per accepted connection.
    /// </summary>
    public Func<Reactor, Connection, Task> Handle = null!;

    public void SubmitConnect(int fd, nint sockaddr, int sockaddrLen, IRingCompletion completion)
    {
        // CONNECT: sockaddr in addr, its length in off, len stays 0.
        SubmitClientOp(IORING_OP_CONNECT, fd, sockaddr, length: 0, offset: sockaddrLen, completion);
    }

    public void SubmitSend(int fd, nint buffer, int length, IRingCompletion completion)
    {
        SubmitClientOp(IORING_OP_SEND, fd, buffer, length, offset: 0, completion);
    }

    public void SubmitRecv(int fd, nint buffer, int length, IRingCompletion completion)
    {
        SubmitClientOp(IORING_OP_RECV, fd, buffer, length, offset: 0, completion);
    }

    public void SubmitRead(int fd, nint buffer, int length, long offset, IRingCompletion completion)
    {
        SubmitClientOp(IORING_OP_READ, fd, buffer, length, offset, completion);
    }

    public void SubmitWrite(int fd, nint buffer, int length, long offset, IRingCompletion completion)
    {
        SubmitClientOp(IORING_OP_WRITE, fd, buffer, length, offset, completion);
    }

    private void SubmitClientOp(byte opcode, int fd, nint buffer, int length, long offset, IRingCompletion completion)
    {
        // Off-reactor callers hand over and wake, like every other producer.
        if (Environment.CurrentManagedThreadId != _reactorThreadId && _reactorThreadId != 0)
        {
            _remoteOps.Enqueue(new RemoteOp(opcode, fd, buffer, length, offset, completion));
            WakeFdWrite();
            return;
        }

        SubmitClientOpCore(opcode, fd, buffer, length, offset, completion);
    }

    private void SubmitClientOpCore(byte opcode, int fd, nint buffer, int length, long offset, IRingCompletion completion)
    {
        int slot = AllocOpSlot(completion);

        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);

        sqe->opcode    = opcode;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buffer;
        sqe->len       = (uint)length;
        sqe->off       = (ulong)offset;
        sqe->user_data = Tag(KindClient, 0, slot);
    }

    private void DrainRemoteOps()
    {
        while (_remoteOps.TryDequeue(out RemoteOp op))
        {
            SubmitClientOpCore(op.Opcode, op.Fd, op.Buffer, op.Length, op.Offset, op.Completion);
        }
    }

    private int AllocOpSlot(IRingCompletion completion)
    {
        if (_opFree == null)
        {
            _opFree = new int[_opTargets.Length];
            for (int i = 0; i < _opFree.Length; i++)
            {
                _opFree[i] = _opFree.Length - 1 - i;
            }
            _opFreeTop = _opFree.Length;
        }

        if (_opFreeTop == 0)
        {
            int oldLength = _opTargets.Length;
            Array.Resize(ref _opTargets, oldLength * 2);
            Array.Resize(ref _opFree, oldLength * 2);
            for (int i = oldLength * 2 - 1; i >= oldLength; i--)
            {
                _opFree[_opFreeTop++] = i;
            }
        }

        int slot = _opFree[--_opFreeTop];
        _opTargets[slot] = completion;
        return slot;
    }
}
