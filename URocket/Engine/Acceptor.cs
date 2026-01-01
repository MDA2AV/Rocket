using System.Text;
using URocket.Engine.Configs;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class Engine {
    public Acceptor SingleAcceptor { get; set; } = null!;
    public class Acceptor {
        private io_uring* _ring;
        private io_uring_sqe* _sqe;
        private readonly Engine _engine;
        private readonly AcceptorConfig _config;
        private readonly io_uring_cqe*[] _cqes;
        private readonly int _listenFd;

        public Acceptor(Engine engine) : this(new AcceptorConfig(), engine) { }

        public Acceptor(AcceptorConfig config, Engine engine) {
            _config = config; 
            _engine = engine;
            _listenFd = CreateListenerSocket(_engine.Ip, _engine.Port); 
            _cqes = new io_uring_cqe*[_config.BatchSqes];
        }

        public void InitRing() {
            _ring = CreateRing(_config.RingFlags, _config.SqCpuThread, _config.SqThreadIdleMs, out int err, _config.RingEntries);
            CheckRingFlags(shim_get_ring_flags(_ring));
            if (_ring == null || err < 0) { Console.Error.WriteLine($"[acceptor] create_ring failed: {err}"); return; }
            // Start multishot accept
            _sqe = SqeGet(_ring);
            shim_prep_multishot_accept(_sqe, _listenFd, SOCK_NONBLOCK);
            shim_sqe_set_data64(_sqe, PackUd(UdKind.Accept, _listenFd));
            shim_submit(_ring);
            Console.WriteLine("[acceptor] Multishot accept armed");
        }

        private void CheckRingFlags(uint flags) {
            Console.WriteLine($"[acceptor] ring flags = 0x{flags:x} " +
                              $"(SQPOLL={(flags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(flags & IORING_SETUP_SQ_AFF) != 0})");
        }
        
        private int CreateListenerSocket(string ip, ushort port) {
            int lfd = socket(AF_INET, SOCK_STREAM, 0);
            int one = 1;

            // TODO: check HRESULT for each libc call
            setsockopt(lfd, SOL_SOCKET, SO_REUSEADDR, &one, (uint)sizeof(int));
            setsockopt(lfd, SOL_SOCKET, SO_REUSEPORT, &one, (uint)sizeof(int));
            // TODO: Is this required?
            setsockopt(lfd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));

            sockaddr_in addr = default;
            addr.sin_family = (ushort)AF_INET;
            addr.sin_port = Htons(port);

            byte[] ipb = Encoding.UTF8.GetBytes(ip + "\0");
            fixed (byte* pip = ipb) inet_pton(AF_INET, (sbyte*)pip, &addr.sin_addr);

            bind(lfd, &addr, (uint)sizeof(sockaddr_in));
            listen(lfd, _engine.Backlog);

            int fl = fcntl(lfd, F_GETFL, 0);
            fcntl(lfd, F_SETFL, fl | O_NONBLOCK);

            return lfd;
        }
        
        public void Handle(Acceptor acceptor, int reactorCount) {
            try {
                int nextReactor = 0;
                int one = 1;
                Console.WriteLine($"[acceptor] Load balancing across {reactorCount} reactors");

                while (_engine.ServerRunning) {
                    int got;
                    fixed (io_uring_cqe** pC = acceptor._cqes)
                        got = shim_peek_batch_cqe(acceptor._ring, pC, (uint)acceptor._cqes.Length);

                    if (got <= 0) {
                        io_uring_cqe* oneCqe = null;
                        if (shim_wait_cqe(acceptor._ring, &oneCqe) != 0) continue;
                        acceptor._cqes[0] = oneCqe;
                        got = 1;
                    }

                    for (int i = 0; i < got; i++) {
                        io_uring_cqe* cqe = acceptor._cqes[i];
                        ulong ud = shim_cqe_get_data64(cqe);
                        UdKind kind = UdKindOf(ud);
                        int res = cqe->res;

                        if (kind == UdKind.Accept) {
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
                                _engine.Connections[targetReactor][clientFd] = _engine.ConnectionPool.Get()
                                    .SetFd(clientFd)
                                    .SetReactor(_engine.Reactors[targetReactor]);
                                
                                bool connectionAdded = _engine.ConnectionQueues.Writer.TryWrite(new ConnectionItem(targetReactor, clientFd));
                                if (!connectionAdded) Console.WriteLine("Failed to write connection!!");
                                
                            }else { Console.WriteLine($"[acceptor] Accept error: {res}"); }
                        }
                        shim_cqe_seen(acceptor._ring, cqe);
                    }
                    if (shim_sq_ready(acceptor._ring) > 0) { Console.WriteLine("S3"); shim_submit(acceptor._ring); }
                }
            }
            finally {
                // close listener and ring even on exception/StopAll
                if (acceptor._listenFd >= 0) close(acceptor._listenFd);
                if (acceptor._ring != null) shim_destroy_ring(acceptor._ring);
                Console.WriteLine($"[acceptor] Shutdown complete.");
            }
        }
    }
    
    private static io_uring* CreateRing(uint flags, int sqThreadCpu, uint sqThreadIdleMs, out int err, uint ringEntries) {
        if(flags == 0)
            return shim_create_ring(ringEntries, out err);
        return shim_create_ring_ex(ringEntries, flags, sqThreadCpu, sqThreadIdleMs, out err);
    }

    // TODO: This seems to be causing segfault when sqe is null
    private static io_uring_sqe* SqeGet(io_uring* pring) {
        io_uring_sqe* sqe = shim_get_sqe(pring);
        if (sqe == null) {
            Console.WriteLine("S4");
            shim_submit(pring); 
            sqe = shim_get_sqe(pring); 
        }
        return sqe;
    }

    private static void ArmRecvMultishot(io_uring* pring, int fd, uint bgid) {
        io_uring_sqe* sqe = SqeGet(pring);
        shim_prep_recv_multishot_select(sqe, fd, bgid, 0);
        shim_sqe_set_data64(sqe, PackUd(UdKind.Recv, fd));
    }
}