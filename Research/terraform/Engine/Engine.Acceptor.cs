using terraform.Engine.Configs;
using terraform.Ring;
using static terraform.ABI.Libc;
using static terraform.Ring.Constants;
// ReSharper disable SuggestVarOrType_Elsewhere
// ReSharper disable SuggestVarOrType_BuiltInTypes
// ReSharper disable SuggestVarOrType_SimpleTypes

namespace terraform.Engine;

public sealed unsafe partial class Engine
{
    public partial class Acceptor
    {
        private IoUring _ring = null!;
        private readonly Engine _engine;
        private readonly AcceptorConfig _acceptorConfig;
        private readonly IoUringCqe*[] _cqes;
        private readonly int _listenFd;

        public Acceptor(Engine engine) : this(new AcceptorConfig(), engine) { }

        public Acceptor(AcceptorConfig acceptorConfig, Engine engine)
        {
            _acceptorConfig = acceptorConfig;
            _engine = engine;
            _listenFd = acceptorConfig.IPVersion == IPVersion.IPv4Only
                ? CreateIPv4ListenerSocket(_engine.Options.Ip, _engine.Options.Port)
                : CreateListenerSocketDualStack(_engine.Options.Ip, _engine.Options.Port);
            _cqes = new IoUringCqe*[_acceptorConfig.BatchSqes];
        }

        public void InitRing()
        {
            _ring = CreateRing(_acceptorConfig.RingFlags, _acceptorConfig.SqCpuThread, _acceptorConfig.SqThreadIdleMs, _acceptorConfig.RingEntries);
            CheckRingFlags(_ring.SetupFlags);

            // Start multishot accept
            IoUringSqe* sqe = SqeGet(_ring);
            Sqe.PrepMultishotAccept(sqe, _listenFd, (uint)SOCK_NONBLOCK);
            sqe->user_data = PackUd(UdKind.Accept, _listenFd);
            _ring.Submit();
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
                KernelTimespec ts;
                ts.tv_sec  = 0;
                ts.tv_nsec = _acceptorConfig.CqTimeout;
                Console.WriteLine($"[acceptor] Load balancing across {reactorCount} reactors");

                while (_engine.ServerRunning)
                {
                    int got;
                    fixed (IoUringCqe** pC = acceptor._cqes)
                        got = acceptor._ring.PeekBatchCqe(pC, acceptor._cqes.Length);

                    if (got <= 0)
                    {
                        acceptor._ring.SubmitAndWaitTimeout(1, &ts);
                        fixed (IoUringCqe** pC = acceptor._cqes)
                            got = acceptor._ring.PeekBatchCqe(pC, 1);
                        if (got <= 0) continue;
                    }

                    for (int i = 0; i < got; i++)
                    {
                        IoUringCqe* cqe = acceptor._cqes[i];
                        ulong ud = cqe->user_data;
                        UdKind kind = UdKindOf(ud);
                        int res = cqe->res;

                        if (kind == UdKind.Accept)
                        {
                            if (res >= 0)
                            {
                                int clientFd = res;
                                setsockopt(clientFd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));

                                int targetReactor = nextReactor;
                                nextReactor = (nextReactor + 1) % reactorCount;

                                ReactorQueues[targetReactor].Enqueue(clientFd);
                            }
                            else { Console.WriteLine($"[acceptor] Accept error: {res}"); }
                        }
                    }
                    acceptor._ring.CqAdvance((uint)got);
                    if (acceptor._ring.SqReady() > 0) { acceptor._ring.Submit(); }
                }
            }
            finally
            {
                if (acceptor._listenFd >= 0) close(acceptor._listenFd);
                acceptor._ring?.Dispose();
                Console.WriteLine($"[acceptor] Shutdown complete.");
            }
        }
    }
}
