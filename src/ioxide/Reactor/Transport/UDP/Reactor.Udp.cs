using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ioxide.Native;

namespace ioxide;

public delegate void UdpDatagramHandler(Reactor reactor, in UdpDatagram datagram);

/// <summary>
/// UDP transport: each reactor binds every <see cref="ServerConfig.UdpPorts"/> port via SO_REUSEPORT
/// (mirroring the TCP listener sharding) and keeps a fixed pool of in-flight RECVMSG slots per
/// socket - a slot re-arms as soon as its datagram has been handled. RECVMSG rather than plain RECV
/// because a shared datagram socket needs the per-packet peer address (msg_name) and the GRO/TOS
/// control messages. Sends go through pooled SENDMSG slots (copy-in), optionally GSO-segmented.
/// QUIC rides this layer (Reactor.Quic.cs).
/// </summary>
public sealed unsafe partial class Reactor
{
    /// <summary>
    /// Per-datagram handler, invoked inline on the reactor thread. Like the TCP <see cref="Handle"/>,
    /// set it before <see cref="Run"/>.
    /// </summary>
    public UdpDatagramHandler? OnDatagram;

    // Slot native block: [msghdr 56][iovec 16][name 128][control][pad][payload 64K]; recv and send
    // share the header/name/iov offsets and differ in control size and payload start. 64 KiB payload
    // because that's the largest train UDP_GRO can coalesce (and the GSO batch ceiling on send);
    // smaller would silently truncate bursts (MSG_TRUNC drops the tail, it doesn't split).
    internal const int UdpNameCap       = 128;
    private const int UdpCtrlCap        = 256;
    private const int UdpPayloadCap     = 64 * 1024;
    private const int UdpIovOff         = 56;
    private const int UdpNameOff        = 72;
    private const int UdpCtrlOff        = 200;
    private const int UdpRecvPayloadOff = 512;
    private const int UdpRecvBlockSize  = UdpRecvPayloadOff + UdpPayloadCap;

    // Send control only needs one UDP_SEGMENT cmsg (CmsgSpace(2) = 24), so payload starts earlier.
    private const int UdpSendPayloadOff = 320;
    private const int UdpSendBlockSize  = UdpSendPayloadOff + UdpPayloadCap;

    private const int ECANCELED = 125;

    private struct UdpRecvSlot
    {
        public nint   Block;
        public int    Fd;
        public ushort Port;
    }

    private int[]         _udpFds       = [];
    private ushort[]      _udpFdPorts   = [];
    private UdpRecvSlot[] _udpRecvSlots = [];
    private int           _udpRecvSlotCount;

    // Send slots: free-list-pooled, grown on demand (same shape as the client-op slot registry).
    private nint[] _udpSendBlocks = [];
    private int[]  _udpSendFree   = [];
    private int    _udpSendFreeTop;
    private int    _udpSendCount;

    private void OpenUdpSockets()
    {
        if (_config.UdpPorts.Length == 0 && _config.Quic == null)
        {
            return;   // no datagram transport configured
        }

        // The QUIC port is a UDP socket like any other; only completion routing differs.
        ushort[] udpPorts = _config.UdpPorts;
        if (_config.Quic is { } quic && Array.IndexOf(udpPorts, quic.Port) < 0)
        {
            udpPorts = [.. udpPorts, quic.Port];
        }

        int ports = udpPorts.Length;
        int slotsPerSocket = _config.UdpRecvSlots;

        _udpFds       = new int[ports];
        _udpFdPorts   = new ushort[ports];
        _udpRecvSlots = new UdpRecvSlot[ports * slotsPerSocket];

        for (int i = 0; i < ports; i++)
        {
            ushort port = udpPorts[i];
            int fd = OpenUdpSocket(port, _config.DualStack, _config.UdpGro);
            _udpFds[i]     = fd;
            _udpFdPorts[i] = port;

            for (int s = 0; s < slotsPerSocket; s++)
            {
                int slot = _udpRecvSlotCount++;
                _udpRecvSlots[slot] = new UdpRecvSlot
                {
                    Block = (nint)NativeMemory.AlignedAlloc(UdpRecvBlockSize, 64),
                    Fd    = fd,
                    Port  = port,
                };
                ArmUdpRecv(slot);
            }
        }
    }

    private static int OpenUdpSocket(ushort port, bool dualStack, bool gro)
    {
        int fd = socket(dualStack ? AF_INET6 : AF_INET, SOCK_DGRAM, 0);
        if (fd < 0)
        {
            throw new InvalidOperationException($"udp socket failed: {fd}");
        }

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(int));
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(int));
        if (gro)
        {
            setsockopt(fd, SOL_UDP, UDP_GRO, &one, sizeof(int));
        }

        if (dualStack)
        {
            int v6only = 0;
            setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, &v6only, sizeof(int));
            // TCLASS covers native IPv6; RECVTOS covers IPv4-mapped peers on the same socket.
            setsockopt(fd, IPPROTO_IPV6, IPV6_RECVTCLASS, &one, sizeof(int));
            setsockopt(fd, IPPROTO_IP, IP_RECVTOS, &one, sizeof(int));

            sockaddr_in6 addr6 = default;
            addr6.sin6_family = AF_INET6;
            addr6.sin6_port   = Htons(port);

            if (bind(fd, &addr6, (uint)sizeof(sockaddr_in6)) < 0)
            {
                throw new InvalidOperationException($"udp bind :{port} failed");
            }
        }
        else
        {
            setsockopt(fd, IPPROTO_IP, IP_RECVTOS, &one, sizeof(int));

            sockaddr_in addr = default;
            addr.sin_family      = AF_INET;
            addr.sin_port        = Htons(port);
            addr.sin_addr.s_addr = 0;

            if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0)
            {
                throw new InvalidOperationException($"udp bind :{port} failed");
            }
        }

        return fd;
    }

    // (Re)build the slot's msghdr and submit its RECVMSG. The kernel clobbers msg_namelen,
    // msg_controllen and msg_flags on every completion, so the header is reset each arm.
    private void ArmUdpRecv(int slot)
    {
        byte* block = (byte*)_udpRecvSlots[slot].Block;

        var iov = (iovec*)(block + UdpIovOff);
        iov->iov_base = block + UdpRecvPayloadOff;
        iov->iov_len  = UdpPayloadCap;

        var m = (msghdr*)block;
        m->msg_name       = block + UdpNameOff;
        m->msg_namelen    = UdpNameCap;
        m->msg_iov        = iov;
        m->msg_iovlen     = 1;
        m->msg_control    = block + UdpCtrlOff;
        m->msg_controllen = UdpCtrlCap;
        m->msg_flags      = 0;

        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECVMSG;
        sqe->fd        = _udpRecvSlots[slot].Fd;
        sqe->addr      = (ulong)m;
        sqe->len       = 1;
        sqe->user_data = Tag(KindUdpRecv, 0, slot);
    }

    private void OnUdpRecvCompletion(int slot, int res)
    {
        if ((uint)slot >= (uint)_udpRecvSlotCount)
        {
            return;
        }

        if (res < 0)
        {
            // Teardown cancels these; anything else is transient (e.g. ICMP-induced errors) - re-arm.
            if (res == -ECANCELED || _stopRequested)
            {
                return;
            }
            Console.Error.WriteLine($"[r{_id}] udp recv error: {res}");
            ArmUdpRecv(slot);
            return;
        }

        ref UdpRecvSlot info = ref _udpRecvSlots[slot];
        byte* block = (byte*)info.Block;
        var m = (msghdr*)block;

        int  gro = 0;
        byte tos = 0;
        for (cmsghdr* c = CmsgFirst(m); c != null; c = CmsgNext(m, c))
        {
            if (c->cmsg_level == SOL_UDP && c->cmsg_type == UDP_GRO)
            {
                gro = *(int*)CmsgData(c);
            }
            else if (c->cmsg_level == IPPROTO_IP && c->cmsg_type == IP_TOS)
            {
                tos = *CmsgData(c);
            }
            else if (c->cmsg_level == IPPROTO_IPV6 && c->cmsg_type == IPV6_TCLASS)
            {
                tos = (byte)*(int*)CmsgData(c);
            }
        }

        if ((m->msg_flags & MSG_TRUNC) != 0)
        {
            // A single datagram larger than 64 KiB is impossible; a train can't exceed it either.
            Console.Error.WriteLine($"[r{_id}] udp datagram truncated (res={res})");
        }

        var datagram = new UdpDatagram(info.Fd, info.Port, (nint)m->msg_name, (int)m->msg_namelen,
                                       new ReadOnlySpan<byte>(block + UdpRecvPayloadOff, res), gro, tos);
        try
        {
            if (_quicOptions != null && info.Port == _quicOptions.Port)
            {
                // QUIC
                QuicDispatch(in datagram);
            }
            else
            {
                // Just UDP
                OnDatagram?.Invoke(this, in datagram);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[r{_id}] datagram handler faulted: {e.GetBaseException().Message}");
        }

        // TODO: Multishot?
        ArmUdpRecv(slot);
    }

    /// <summary>
    /// Send one datagram (copy-in) to <paramref name="peerAddr"/> on <paramref name="socketFd"/>.
    /// With <paramref name="gsoSegmentSize"/> &gt; 0 the payload is a batch the kernel splits into
    /// wire datagrams of that size (UDP_SEGMENT) - one submission, many packets. Reactor-thread-only
    /// (handlers and OnStart already are; marshal via <see cref="ScheduleOnReactor"/> otherwise).
    /// </summary>
    public void UdpSendTo(int socketFd, nint peerAddr, int peerAddrLen, ReadOnlySpan<byte> payload, int gsoSegmentSize = 0)
    {
        if (!OnReactorThread && _reactorThreadId != 0)
        {
            throw new InvalidOperationException("UdpSendTo must run on the reactor thread (marshal via ScheduleOnReactor).");
        }
        if (payload.Length > UdpPayloadCap)
        {
            throw new ArgumentException($"payload {payload.Length} exceeds the {UdpPayloadCap} send cap", nameof(payload));
        }
        if ((uint)peerAddrLen > UdpNameCap)
        {
            throw new ArgumentException($"peer sockaddr length {peerAddrLen} exceeds {UdpNameCap}", nameof(peerAddrLen));
        }

        int slot = AllocUdpSendSlot();
        byte* block = (byte*)_udpSendBlocks[slot];

        Buffer.MemoryCopy((void*)peerAddr, block + UdpNameOff, UdpNameCap, peerAddrLen);
        payload.CopyTo(new Span<byte>(block + UdpSendPayloadOff, UdpPayloadCap));

        var iov = (iovec*)(block + UdpIovOff);
        iov->iov_base = block + UdpSendPayloadOff;
        iov->iov_len  = (nuint)payload.Length;

        var m = (msghdr*)block;
        m->msg_name    = block + UdpNameOff;
        m->msg_namelen = (uint)peerAddrLen;
        m->msg_iov     = iov;
        m->msg_iovlen  = 1;
        m->msg_flags   = 0;

        if (gsoSegmentSize > 0)
        {
            var c = (cmsghdr*)(block + UdpCtrlOff);
            c->cmsg_len   = CmsgHdrLen + sizeof(ushort);
            c->cmsg_level = SOL_UDP;
            c->cmsg_type  = UDP_SEGMENT;
            *(ushort*)CmsgData(c) = (ushort)gsoSegmentSize;
            m->msg_control    = c;
            m->msg_controllen = CmsgSpace(sizeof(ushort));
        }
        else
        {
            m->msg_control    = null;
            m->msg_controllen = 0;
        }

        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_SENDMSG;
        sqe->fd        = socketFd;
        sqe->addr      = (ulong)m;
        sqe->len       = 1;
        sqe->user_data = Tag(KindUdpSend, 0, slot);
    }

    private void OnUdpSendCompletion(int slot, int res)
    {
        if (res < 0)
        {
            Console.Error.WriteLine($"[r{_id}] udp send error: {res}");
        }
        _udpSendFree[_udpSendFreeTop++] = slot;
    }

    private int AllocUdpSendSlot()
    {
        if (_udpSendFreeTop == 0)
        {
            GrowUdpSendSlots();
        }
        return _udpSendFree[--_udpSendFreeTop];
    }

    private void GrowUdpSendSlots()
    {
        int add = _udpSendCount == 0 ? 16 : _udpSendCount;
        int newCount = _udpSendCount + add;

        Array.Resize(ref _udpSendBlocks, newCount);
        Array.Resize(ref _udpSendFree, newCount);
        for (int i = _udpSendCount; i < newCount; i++)
        {
            _udpSendBlocks[i] = (nint)NativeMemory.AlignedAlloc(UdpSendBlockSize, 64);
            _udpSendFree[_udpSendFreeTop++] = i;
        }
        _udpSendCount = newCount;
    }

    // Split teardown: fds close while the ring is still alive (in-flight RECVMSG ops surface as
    // errors/cancels and are dropped); the native blocks are freed only after the ring fd is
    // closed, once the kernel holds no references into them (same discipline as the buffer slab).
    private void CloseUdpFds()
    {
        foreach (int fd in _udpFds)
        {
            close(fd);
        }
    }

    private void FreeUdpMemory()
    {
        for (int i = 0; i < _udpRecvSlotCount; i++)
        {
            NativeMemory.AlignedFree((void*)_udpRecvSlots[i].Block);
        }
        _udpRecvSlotCount = 0;
        _udpRecvSlots = [];

        for (int i = 0; i < _udpSendCount; i++)
        {
            NativeMemory.AlignedFree((void*)_udpSendBlocks[i]);
        }
        _udpSendCount = 0;
        _udpSendFreeTop = 0;
        _udpSendBlocks = [];
        _udpSendFree = [];
    }
}
