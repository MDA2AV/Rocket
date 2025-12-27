using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using static Rocket.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace Rocket.Engine;

public sealed unsafe partial class RocketEngine {
    private static readonly ObjectPool<Connection> ConnectionPool =
        new DefaultObjectPool<Connection>(new ConnectionPoolPolicy(), 1024 * 32);

    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection> {
        public override Connection Create() => new();
        public override bool Return(Connection connection) { connection.Clear(); return true; }
    }
    
    public static Reactor[] s_Reactors = null!;
    public static Dictionary<int, Connection>[] Connections = null!;
    
    public class Reactor
    {
        public int Counter = 0;
        public uint PRingFlags = IORING_SETUP_SQPOLL;
        public int sqThreadCpu = -1;
        public uint sqThreadIdleMs = 100;
        
        public Reactor(int reactorId) { ReactorId = reactorId; }
        
        internal readonly int ReactorId;

        public io_uring* PRing;
        internal io_uring_buf_ring* BufferRing;
        internal byte* BufferRingSlab;
        internal uint BufferRingIndex = 0;
        internal uint BufferRingMask;

        internal void InitPRing()
        {
            //PRing = shim_create_ring((uint)s_ringEntries, out var err);
            /*const uint flags = IORING_SETUP_SQPOLL;
            // Pin SQPOLL thread to CPU 0 (for example) and let it idle 2000ms before sleeping.
            int  sqThreadCpu     = ReactorId;
            uint sqThreadIdleMs  = 2000;
            PRing = shim_create_ring_ex((uint)s_ringEntries, flags, sqThreadCpu, sqThreadIdleMs, out int err);
            */
            
            PRing = CreatePRing(PRingFlags, sqThreadCpu, sqThreadIdleMs, out int err);
            
            uint ringFlags = shim_get_ring_flags(PRing);
            
            Console.WriteLine($"[w{ReactorId}] ring flags = 0x{ringFlags:x} " +
                              $"(SQPOLL={(ringFlags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(ringFlags & IORING_SETUP_SQ_AFF) != 0})");
            if (PRing == null || err < 0) { Console.Error.WriteLine($"[w{ReactorId}] create_ring failed: {err}"); return; }
            
            // Setup buffer ring
            // TODO: Investigate this c_bufferRingGID
            BufferRing = shim_setup_buf_ring(PRing, (uint)s_bufferRingEntries, c_bufferRingGID, 0, out var ret);
            if (BufferRing == null || ret < 0) throw new Exception($"setup_buf_ring failed: ret={ret}");

            BufferRingMask = (uint)(s_bufferRingEntries - 1);
            nuint slabSize = (nuint)(s_bufferRingEntries * s_recvBufferSize);
            BufferRingSlab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);

            for (ushort bid = 0; bid < s_bufferRingEntries; bid++) {
                byte* addr = BufferRingSlab + (nuint)bid * (nuint)s_recvBufferSize;
                shim_buf_ring_add(BufferRing, addr, (uint)s_recvBufferSize, bid, (ushort)BufferRingMask, BufferRingIndex++);
            }
            shim_buf_ring_advance(BufferRing, (uint)s_bufferRingEntries);
        }

        public void ReturnBufferRing(byte* addr, ushort bid) {
            shim_buf_ring_add(BufferRing, addr, (uint)s_recvBufferSize, bid, (ushort)BufferRingMask, BufferRingIndex++);
            shim_buf_ring_advance(BufferRing, 1);
        }
    }
    
    private static void CloseAll(Dictionary<int, Connection> connections) {
        foreach (var connection in connections) {
            try { close(connection.Value.Fd); ConnectionPool.Return(connection.Value); } catch { /* ignore */ }
        }
    }
}