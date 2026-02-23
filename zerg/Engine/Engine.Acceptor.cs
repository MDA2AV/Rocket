using zerg.Engine.Configs;
using static zerg.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace zerg.Engine;

public sealed unsafe partial class Engine 
{
    public partial class Acceptor 
    {
        /// <summary>
        /// The acceptor's io_uring instance used to receive CQEs for accepted sockets.
        /// This ring is dedicated to accepting connections (separate from per-reactor rings).
        /// </summary>
        private io_uring* _io_uring;
        /// <summary>
        /// Cached SQE used during initialization to arm multishot accept.
        /// (After arming, accepts arrive as CQEs without re-submitting an SQE each time.)
        /// </summary>
        private io_uring_sqe* _sqe;
        /// <summary>
        /// Back-reference to the owning engine. Provides configuration (Ip/Port/Backlog),
        /// reactor queues, and the global ServerRunning flag.
        /// </summary>
        private readonly Engine _engine;
        /// <summary>
        /// Acceptor configuration (ring flags, ring size, CQ wait timeout, batch size, etc.).
        /// </summary>
        private readonly AcceptorConfig _acceptorConfig;
        /// <summary>
        /// Scratch array used to batch CQE pointers returned by shim_peek_batch_cqe().
        /// Reused every loop to avoid allocations.
        /// </summary>
        private readonly io_uring_cqe*[] _cqes;
        /// <summary>
        /// Listening socket file descriptor. This is the fd passed to multishot accept.
        /// The listener may be IPv4-only or IPv6 dual-stack depending on configuration.
        /// </summary>
        private readonly int _listenFd;

        public Acceptor(Engine engine) : this(new AcceptorConfig(), engine) { }
        /// <summary>
        /// Creates an acceptor that owns:
        ///  - a listening socket (IPv4-only or IPv6 dual-stack)
        ///  - a CQE batch buffer for the accept loop
        /// The io_uring itself is created later in InitRing().
        /// </summary>
        public Acceptor(AcceptorConfig acceptorConfig, Engine engine) 
        {
            _acceptorConfig = acceptorConfig; 
            _engine = engine;
            _listenFd = acceptorConfig.IPVersion == IPVersion.IPv4Only 
                ? CreateIPv4ListenerSocket(_engine.Options.Ip, _engine.Options.Port) 
                : CreateListenerSocketDualStack(_engine.Options.Ip, _engine.Options.Port);
            _cqes = new io_uring_cqe*[_acceptorConfig.BatchSqes];
        }
        /// <summary>
        /// Initializes the acceptor io_uring and arms multishot accept on the listening socket.
        ///
        /// After this runs successfully, accepted client sockets arrive as CQEs of kind UdKind.Accept,
        /// with cqe->res containing the accepted client fd (or a negative errno on failure).
        /// </summary>
        public void InitRing() 
        {
            _io_uring = CreateRing(_acceptorConfig.RingFlags, _acceptorConfig.SqCpuThread, _acceptorConfig.SqThreadIdleMs, out int err, _acceptorConfig.RingEntries);
            CheckRingFlags(shim_get_ring_flags(_io_uring));
            if (_io_uring == null || err < 0) { Console.Error.WriteLine($"[acceptor] create_ring failed: {err}"); return; }
            // Start multishot accept
            _sqe = SqeGet(_io_uring);
            shim_prep_multishot_accept(_sqe, _listenFd, SOCK_NONBLOCK);
            shim_sqe_set_data64(_sqe, PackUd(UdKind.Accept, _listenFd));
            shim_submit(_io_uring);
            Console.WriteLine("[acceptor] Multishot accept armed");
        }

        private void CheckRingFlags(uint flags) 
        {
            Console.WriteLine($"[acceptor] ring flags = 0x{flags:x} " +
                              $"(SQPOLL={(flags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(flags & IORING_SETUP_SQ_AFF) != 0})");
        }
        
        public void Handle(Acceptor acceptor, int reactorCount) 
        {
            try 
            {
                int nextReactor = 0;
                int one = 1;
                __kernel_timespec ts;
                ts.tv_sec  = 0;
                ts.tv_nsec = _acceptorConfig.CqTimeout;
                Console.WriteLine($"[acceptor] Load balancing across {reactorCount} reactors");

                while (_engine.ServerRunning) 
                {
                    int got;
                    fixed (io_uring_cqe** pC = acceptor._cqes)
                        got = shim_peek_batch_cqe(acceptor._io_uring, pC, (uint)acceptor._cqes.Length);

                    if (got <= 0) 
                    {
                        io_uring_cqe* oneCqe = null;
                        //if (shim_wait_cqe(acceptor._io_uring, &oneCqe) != 0) continue;
                        if (shim_wait_cqe_timeout(acceptor._io_uring, &oneCqe, &ts) != 0) continue;
                        acceptor._cqes[0] = oneCqe;
                        got = 1;
                    }

                    for (int i = 0; i < got; i++) 
                    {
                        io_uring_cqe* cqe = acceptor._cqes[i];
                        ulong ud = shim_cqe_get_data64(cqe);
                        UdKind kind = UdKindOf(ud);
                        int res = cqe->res;

                        if (kind == UdKind.Accept) 
                        {
                            if (res >= 0) {
                                int clientFd = res;
                                setsockopt(clientFd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));

                                // Round-robin to next reactor
                                // TODO: This is naive, not all connections are the same,
                                // TODO: should balance considering each connection's weight
                                // TODO: Allow user to inject balancing logic and provide multiple algorithms he can choose from
                                int targetReactor = nextReactor;
                                nextReactor = (nextReactor + 1) % reactorCount;

                                ReactorQueues[targetReactor].Enqueue(clientFd);
                                
                            }else { Console.WriteLine($"[acceptor] Accept error: {res}"); }
                        }
                        shim_cqe_seen(acceptor._io_uring, cqe);
                    }
                    if (shim_sq_ready(acceptor._io_uring) > 0) { Console.WriteLine("S3"); shim_submit(acceptor._io_uring); }
                }
            }
            finally 
            {
                // close listener and ring even on exception/StopAll
                if (acceptor._listenFd >= 0) close(acceptor._listenFd);
                if (acceptor._io_uring != null) shim_destroy_ring(acceptor._io_uring);
                Console.WriteLine($"[acceptor] Shutdown complete.");
            }
        }
    }
}