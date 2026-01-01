using System.Runtime.InteropServices;

namespace URocket.ABI;

public static unsafe partial class ABI{
    // ------------------------------------------------------------------------------------
    //  IO_URING OPAQUE TYPES
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Opaque handle for an io_uring instance.
    /// <para>
    /// The actual <c>struct io_uring</c> is managed by liburing; we just need a stable
    /// size and pointer we can pass back to the shim. The fixed dummy array ensures the
    /// struct is not empty (so C# can pin/hand a pointer to native code).
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct io_uring { 
        internal fixed ulong _[128]; // DO NOT touch: keeps the struct non-empty/opaque.
    }
    /// <summary>
    /// Submission Queue Entry (opaque fields).
    /// <para>
    /// io_uring SQEs are populated exclusively through shim helpers (prep_* functions).
    /// We never manipulate the layout here; we only pass a pointer back to native code.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct io_uring_sqe {
        internal fixed ulong _[8]; // Opaque view; size suffices for pointer passing.
    }
    /// <summary>
    /// Completion Queue Entry. Only the public fields we read are exposed.
    /// <list type="bullet">
    /// <item><description><c>user_data</c>: 64-bit token we previously attached to the SQE.</description></item>
    /// <item><description><c>res</c>: result (e.g., number of bytes, or -errno on error).</description></item>
    /// <item><description><c>flags</c>: CQE flags (e.g., buffer selection indicators).</description></item>
    /// </list>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct io_uring_cqe {
        internal ulong user_data;
        internal int   res;
        internal uint  flags;
    }
    /// <summary>
    /// Opaque token representing a <c>io_uring_buf_ring</c>.
    /// <para>
    /// All manipulation is done via shim functions; we never dereference this in C#.
    /// </para>
    /// </summary>
    public struct io_uring_buf_ring { } // opaque on purpose
    // ------------------------------------------------------------------------------------
    //  SHIM: RING LIFECYCLE
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Creates an io_uring instance with <paramref name="entries"/> queue size.
    /// <para>
    /// Returns a pointer to an initialized ring or <c>null</c> on error.
    /// The out parameter <paramref name="err"/> carries the negative errno on failure.
    /// </para>
    /// <remarks>
    /// Ownership: the returned pointer must be destroyed via <see cref="shim_destroy_ring"/>.
    /// </remarks>
    /// </summary>
    [DllImport("uringshim")] internal static extern io_uring* shim_create_ring(uint entries, out int err);
    [DllImport("uringshim")] internal static extern uint shim_get_ring_flags(io_uring* ring);
    [DllImport("uringshim")] internal static extern io_uring* shim_create_ring_ex(
        uint entries,
        uint flags,
        int  sq_thread_cpu,
        uint sq_thread_idle_ms,
        out int err);
    /// <summary>
    /// Destroys a ring created with <see cref="shim_create_ring"/> and releases native resources.
    /// Safe to call with <c>null</c>.
    /// </summary>
    [DllImport("uringshim")] internal static extern void shim_destroy_ring(io_uring* ring);
    // ------------------------------------------------------------------------------------
    //  SHIM: QUEUE OPS
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Acquires a fresh SQE from the ring (may return <c>null</c> if SQ is full).
    /// </summary>
    [DllImport("uringshim")] internal static extern io_uring_sqe* shim_get_sqe(io_uring* ring);
    /// <summary>
    /// Submits pending SQEs to the kernel. Returns number of submitted entries or -errno.
    /// </summary>
    [DllImport("uringshim")] internal static extern int shim_submit(io_uring* ring);
    /// <summary>
    /// Blocks until a CQE is available. Returns 0 on success or -errno on error/interrupt.
    /// The out <paramref name="cqe"/> receives the pointer to the available CQE.
    /// </summary>
    [DllImport("uringshim")] internal static extern int shim_wait_cqe(io_uring* ring, io_uring_cqe** cqe);
    /// <summary>
    /// Non-blocking peek for up to <paramref name="count"/> CQEs into the given buffer.
    /// Returns the number of CQEs copied (0 if none available), or -errno on failure.
    /// </summary>
    [DllImport("uringshim")] internal static extern int shim_peek_batch_cqe(io_uring* ring, io_uring_cqe** cqes, uint count);
    /// <summary>
    /// Marks a CQE as seen/consumed, advancing the ring's CQ head.
    /// </summary>
    [DllImport("uringshim")] internal static extern void shim_cqe_seen(io_uring* ring, io_uring_cqe* cqe);
    /// <summary>
    /// Returns how many SQEs are ready (available) to be filled without blocking.
    /// </summary>
    [DllImport("uringshim")] internal static extern uint shim_sq_ready(io_uring* ring);
    // ------------------------------------------------------------------------------------
    //  SHIM: PREP OPS (SQE FILLERS)
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Prepares a multishot <c>accept(2)</c> SQE on <paramref name="lfd"/>.
    /// <para>
    /// Multishot accept emits multiple CQEs over time without resubmitting after each accept.
    /// <paramref name="flags"/> maps to <c>accept4</c> flags (e.g., <c>SOCK_NONBLOCK</c>).
    /// </para>
    /// </summary>
    [DllImport("uringshim")] internal static extern void shim_prep_multishot_accept(io_uring_sqe* sqe, int lfd, int flags);
    /// <summary>
    /// Prepares a multishot <c>recv</c> using buffer selection (buf-ring).
    /// <para>
    /// <paramref name="buf_group"/> is the buffer group id (bgid) registered for selection.
    /// <paramref name="flags"/> maps to <c>recv</c> flags (e.g., <c>MSG_WAITALL</c>).
    /// </para>
    /// </summary>
    [DllImport("uringshim")] internal static extern void shim_prep_recv_multishot_select(io_uring_sqe* sqe, int fd, uint buf_group, int flags);
    /// <summary>
    /// Prepares a <c>send(2)</c> on <paramref name="fd"/> writing <paramref name="nbytes"/> from <paramref name="buf"/>.
    /// <paramref name="flags"/> maps to <c>send</c> flags (e.g., <c>MSG_MORE</c>).
    /// </summary>
    [DllImport("uringshim")] internal static extern void shim_prep_send(io_uring_sqe* sqe, int fd, void* buf, uint nbytes, int flags);
    // SHIM: PREP OPS (SQE FILLERS)
    [DllImport("uringshim")] internal static extern void shim_prep_cancel64(io_uring_sqe* sqe, ulong user_data, int flags);
    // ------------------------------------------------------------------------------------
    //  SHIM: USERDATA HELPERS
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Attaches a 64-bit user token to an SQE (visible later via CQE.user_data).
    /// </summary>
    [DllImport("uringshim")] internal static extern void  shim_sqe_set_data64(io_uring_sqe* sqe, ulong data);
    /// <summary>
    /// Reads the 64-bit user token previously attached to the SQE that completed.
    /// Equivalent to reading <see cref="io_uring_cqe.user_data"/>.
    /// </summary>
    [DllImport("uringshim")] internal static extern ulong shim_cqe_get_data64(io_uring_cqe* cqe);
    // ------------------------------------------------------------------------------------
    //  SHIM: BUF-RING HELPERS (BUFFER SELECTION)
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Sets up (allocates and registers) a buffer ring with <paramref name="entries"/> slots.
    /// <para>
    /// Returns a non-null pointer on success. The ring is associated with <paramref name="bgid"/>.
    /// The out <paramref name="ret"/> is 0 on success or -errno on failure.
    /// </para>
    /// <remarks>
    /// Ownership: free with <see cref="shim_free_buf_ring"/>.
    /// </remarks>
    /// </summary>
    [DllImport("uringshim")] internal static extern io_uring_buf_ring* shim_setup_buf_ring(io_uring* ring, uint entries, uint bgid, uint flags, out int ret);
    /// <summary>
    /// Frees/unregisters a previously created buffer ring.
    /// Safe to call with null arguments.
    /// </summary>
    [DllImport("uringshim")] internal static extern void shim_free_buf_ring(io_uring* ring, io_uring_buf_ring* br, uint entries, uint bgid);
    /// <summary>
    /// Adds a buffer into the ring at logical index <paramref name="idx"/>.
    /// <para>
    /// <paramref name="addr"/> and <paramref name="len"/> define the buffer;
    /// <paramref name="bid"/> is the 16-bit buffer id returned in CQEs;
    /// <paramref name="mask"/> must be <c>entries - 1</c> for power-of-two rings.
    /// </para>
    /// </summary>
    [DllImport("uringshim")] internal static extern void shim_buf_ring_add(io_uring_buf_ring* br, void* addr, uint len, ushort bid, ushort mask, uint idx);
    /// <summary>
    /// Advances the visible producer index by <paramref name="count"/> after one or more adds.
    /// </summary>
    [DllImport("uringshim")] internal static extern void shim_buf_ring_advance(io_uring_buf_ring* br, uint count);
    /// <summary>
    /// Returns non-zero if the CQE indicates a provided buffer was used.
    /// </summary>
    [DllImport("uringshim")] internal static extern int shim_cqe_has_buffer(io_uring_cqe* cqe);
    /// <summary>
    /// When <see cref="shim_cqe_has_buffer"/> is non-zero, returns the selected buffer id (bid).
    /// </summary>
    [DllImport("uringshim")] internal static extern uint shim_cqe_buffer_id(io_uring_cqe* cqe);
    /// <summary>
    /// Waits for a CQE with a timeout specified via <see cref="__kernel_timespec"/>.
    /// <para>
    /// Returns 0 on success (and sets <paramref name="cqe"/>) or -errno:
    /// e.g. <c>-ETIME</c> when the timeout expires, <c>-EINTR</c> when interrupted.
    /// </para>
    /// </summary>
    [DllImport("uringshim")] internal static extern int shim_wait_cqe_timeout(io_uring* ring, io_uring_cqe** cqe, __kernel_timespec* ts);
    [DllImport("uringshim")] internal static extern int shim_wait_cqes(
        io_uring* ring,
        io_uring_cqe** cqe,         // pointer to first CQE slot (array start)
        uint waitNr,                // how many CQEs we'd *like* to wait for
        __kernel_timespec* ts);     // timeout (or null for infinite) 
    // ------------------------------------------------------------------------------------
    //  USERDATA PACKING (CQE.user_data)
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Identifies what kind of operation a CQE corresponds to.
    /// </summary>
    internal enum UdKind : uint {
        Accept = 1,
        Recv   = 2,
        Send   = 3,
        Cancel = 4
    }
    /// <summary>
    /// Packs a kind + fd into a single 64-bit token suitable for <see cref="io_uring_sqe"/>.
    /// <para>
    /// Upper 32 bits: <see cref="UdKind"/>; lower 32 bits: file descriptor.
    /// </para>
    /// </summary>
    internal static ulong PackUd(UdKind k, int fd) => ((ulong)k << 32) | (uint)fd;
    /// <summary>
    /// Extracts the kind from a packed user_data token.
    /// </summary>
    internal static UdKind UdKindOf(ulong ud) => (UdKind)(ud >> 32);
    /// <summary>
    /// Extracts the file descriptor from a packed user_data token.
    /// </summary>
    internal static int UdFdOf(ulong ud) => (int)(ud & 0xffffffff);
    
    internal const uint IORING_SETUP_IOPOLL  = 1u << 0;
    internal const uint IORING_SETUP_SQPOLL  = 1u << 1;
    internal const uint IORING_SETUP_SQ_AFF  = 1u << 2;
    
    internal const uint IORING_CQE_F_MORE = (1U << 1);
    
    internal const int IORING_ASYNC_CANCEL_ALL = 1 << 0; // commonly this value
}