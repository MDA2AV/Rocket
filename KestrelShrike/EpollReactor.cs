namespace KestrelShrike;

/// <summary>
/// One reactor = one thread + one epoll instance + its own SO_REUSEPORT listener
/// (Minima/Shrike topology — kernel balances accepts, no acceptor thread). It is
/// purely a readiness driver: EPOLLIN → drain recv into the connection's input
/// Pipe (Kestrel reads it); EPOLLOUT → wake a pump that hit EAGAIN; error/hup →
/// close. Response sends happen on the pump's thread, not here.
/// </summary>
internal sealed unsafe class EpollReactor
{
    public readonly int Id;
    private readonly ushort _port;
    private readonly int _backlog;
    private readonly int _maxEvents;

    private int _ep;
    private int _listenFd;
    private readonly ConcurrentDictionary<int, EpollConnection> _conns = new();
    private volatile bool _running = true;

    internal Action<EpollConnection>? OnAccept;

    public EpollReactor(int id, ushort port, int backlog, int maxEvents)
    {
        Id = id;
        _port = port;
        _backlog = backlog;
        _maxEvents = maxEvents;
    }

    public void Stop() => _running = false;

    internal void Remove(EpollConnection conn) =>
        _conns.TryRemove(new KeyValuePair<int, EpollConnection>(conn.Fd, conn));

    public void Run()
    {
        _ep = epoll_create1(EPOLL_CLOEXEC);
        if (_ep < 0) throw new Exception("epoll_create1 failed");
        _listenFd = OpenReusePortListener(_port, _backlog);

        byte* lev = stackalloc byte[EvSize];
        WriteEpollEvent(lev, (uint)(EPOLLIN | EPOLLERR | EPOLLHUP), _listenFd);
        if (epoll_ctl(_ep, EPOLL_CTL_ADD, _listenFd, (IntPtr)lev) != 0)
            throw new Exception("epoll_ctl ADD listen failed");

        IntPtr eventsBuf = Marshal.AllocHGlobal(EvSize * _maxEvents);
        Console.WriteLine($"[shrike-k r{Id}] listening on 0.0.0.0:{_port}");

        while (_running)
        {
            int n = epoll_wait(_ep, eventsBuf, _maxEvents, -1);
            if (n < 0) { if (Marshal.GetLastPInvokeError() == EINTR) continue; break; }

            for (int i = 0; i < n; i++)
            {
                ReadEpollEvent((byte*)eventsBuf + i * EvSize, out uint evs, out int fd);

                if (fd == _listenFd) { AcceptLoop(); continue; }

                if (!_conns.TryGetValue(fd, out var conn)) continue;

                if ((evs & (uint)(EPOLLERR | EPOLLHUP | EPOLLRDHUP)) != 0)
                {
                    Close(conn);
                    continue;
                }

                if ((evs & (uint)EPOLLIN) != 0)
                {
                    if (!conn.OnReadable()) { Close(conn); continue; }
                }

                if ((evs & (uint)EPOLLOUT) != 0)
                {
                    conn.SignalWritable();
                }
            }
        }

        Marshal.FreeHGlobal(eventsBuf);
        close(_listenFd);
        close(_ep);
    }

    private void AcceptLoop()
    {
        for (;;)
        {
            int cfd = accept4(_listenFd, IntPtr.Zero, IntPtr.Zero, SOCK_NONBLOCK | SOCK_CLOEXEC);
            if (cfd >= 0)
            {
                int one = 1;
                setsockopt(cfd, IPPROTO_TCP, TCP_NODELAY, ref one, sizeof(int));

                byte* ev = stackalloc byte[EvSize];
                WriteEpollEvent(ev, (uint)(EPOLLIN | EPOLLRDHUP | EPOLLERR | EPOLLHUP) | EPOLLET, cfd);
                epoll_ctl(_ep, EPOLL_CTL_ADD, cfd, (IntPtr)ev);

                var c = new EpollConnection(cfd, _ep, this);
                _conns[cfd] = c;
                OnAccept?.Invoke(c);
                _ = c.RunOutputPump();
                continue;
            }
            int err = Marshal.GetLastPInvokeError();
            if (err == EINTR) continue;
            break;   // EAGAIN/EWOULDBLOCK (drained) or transient error
        }
    }

    private static void Close(EpollConnection conn)
    {
        conn.MarkClosed();   // complete Input.Writer (reactor is its sole writer)
        conn.DecRef();       // reactor/recv side done
    }

    private static int OpenReusePortListener(ushort port, int backlog)
    {
        int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, IPPROTO_TCP);
        if (fd < 0) throw new Exception($"socket failed errno={Marshal.GetLastPInvokeError()}");

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, ref one, sizeof(int));
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, ref one, sizeof(int));

        int fl = fcntl(fd, F_GETFL, 0);
        if (fl >= 0) fcntl(fd, F_SETFL, fl | O_NONBLOCK);

        var addr = new sockaddr_in
        {
            sin_family = (ushort)AF_INET,
            sin_port   = Htons(port),
            sin_addr   = new in_addr { s_addr = 0 },
            sin_zero   = new byte[8]
        };
        if (bind(fd, ref addr, (uint)Marshal.SizeOf<sockaddr_in>()) != 0)
            throw new Exception($"bind failed errno={Marshal.GetLastPInvokeError()}");
        if (listen(fd, backlog) != 0)
            throw new Exception($"listen failed errno={Marshal.GetLastPInvokeError()}");
        return fd;
    }
}
