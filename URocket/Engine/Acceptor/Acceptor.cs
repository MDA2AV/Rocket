using URocket.Engine.Builder;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class RocketEngine {
    public class Acceptor {
        private io_uring_sqe* _sqe;
        private readonly AcceptorConfig _config;
        
        public io_uring* Ring { get; private set; }
        public int ListenFd { get; }

        public Acceptor() : this(new  AcceptorConfig()) { }
        public Acceptor(AcceptorConfig config) { _config = config; ListenFd = CreateListenerSocket(c_ip, s_port); }

        public void InitRing() {
            Ring = CreateRing(_config.RingFlags, _config.SqCpuThread, _config.SqThreadIdleMs, out int err, _config.RingEntries);
            CheckRingFlags(shim_get_ring_flags(Ring));
            if (Ring == null || err < 0) { Console.Error.WriteLine($"[acceptor] create_ring failed: {err}"); return; }
            // Start multishot accept
            _sqe = SqeGet(Ring);
            shim_prep_multishot_accept(_sqe, ListenFd, SOCK_NONBLOCK);
            shim_sqe_set_data64(_sqe, PackUd(UdKind.Accept, ListenFd));
            shim_submit(Ring);
            Console.WriteLine("[acceptor] Multishot accept armed");
        }

        private void CheckRingFlags(uint flags) {
            Console.WriteLine($"[acceptor] ring flags = 0x{flags:x} " +
                              $"(SQPOLL={(flags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(flags & IORING_SETUP_SQ_AFF) != 0})");
        }
    }
}