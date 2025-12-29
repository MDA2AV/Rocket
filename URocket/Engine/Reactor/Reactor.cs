using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using URocket;
using URocket.Engine.Builder;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class RocketEngine {
    private static readonly ObjectPool<Connection> ConnectionPool =
        new DefaultObjectPool<Connection>(new ConnectionPoolPolicy(), 1024 * 32);

    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection> {
        public override Connection Create() => new();
        public override bool Return(Connection connection) { connection.Clear(); return true; }
    }
    
    // TODO No static here
    public static Reactor[] s_Reactors = null!;
    public static Dictionary<int, Connection>[] Connections = null!;
    
    public class Reactor
    {
        public int Counter = 0;

        public Reactor(int reactorId, ReactorConfig config) { ReactorId = reactorId; Config = config; }
        public Reactor(int reactorId) : this(reactorId, new ReactorConfig()) { }

        internal int ReactorId { get; }
        public ReactorConfig Config { get; }
        public io_uring* Ring;
        internal io_uring_buf_ring* BufferRing { get; set; }
        internal byte* BufferRingSlab { get; set; }
        internal uint BufferRingIndex { get; set; } = 0;
        internal uint BufferRingMask { get; private set; }

        public void InitRing()
        {
            Ring = CreateRing(Config.RingFlags, Config.SqCpuThread, Config.SqThreadIdleMs, out int err, Config.RingEntries);
            uint ringFlags = shim_get_ring_flags(Ring);
            Console.WriteLine($"[w{ReactorId}] ring flags = 0x{ringFlags:x} " +
                              $"(SQPOLL={(ringFlags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(ringFlags & IORING_SETUP_SQ_AFF) != 0})");
            if (Ring == null || err < 0) { Console.Error.WriteLine($"[w{ReactorId}] create_ring failed: {err}"); return; }
            
            BufferRing = shim_setup_buf_ring(Ring, (uint)Config.BufferRingEntries, c_bufferRingGID, 0, out var ret);
            if (BufferRing == null || ret < 0) throw new Exception($"setup_buf_ring failed: ret={ret}");

            BufferRingMask = (uint)(Config.BufferRingEntries - 1);
            nuint slabSize = (nuint)(Config.BufferRingEntries * Config.RecvBufferSize);
            BufferRingSlab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);

            for (ushort bid = 0; bid < Config.BufferRingEntries; bid++) {
                byte* addr = BufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                shim_buf_ring_add(BufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)BufferRingMask, BufferRingIndex++);
            }
            shim_buf_ring_advance(BufferRing, (uint)Config.BufferRingEntries);
        }

        public void ReturnBufferRing(byte* addr, ushort bid) {
            shim_buf_ring_add(BufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)BufferRingMask, BufferRingIndex++);
            shim_buf_ring_advance(BufferRing, 1);
        }
    }
    
    private static void CloseAll(Dictionary<int, Connection> connections) {
        foreach (var connection in connections) {
            try { close(connection.Value.Fd); ConnectionPool.Return(connection.Value); } catch { /* ignore */ }
        }
    }
}