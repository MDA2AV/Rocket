using System.Runtime.InteropServices;

namespace zerg.ABI;

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
    //  SHIM: QUEUE OPS (ADVANCED / HIGH-PERF)
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Submits all currently queued SQEs to the kernel and then blocks
    /// until at least <paramref name="waitNr"/> CQEs are available.
    ///
    /// <para>
    /// This is equivalent to a combined <c>submit + wait</c> operation and
    /// results in a <b>single</b> <c>io_uring_enter()</c> syscall.
    /// </para>
    ///
    /// <para>
    /// Using this instead of separate <see cref="shim_submit"/> and
    /// <see cref="shim_wait_cqes"/> calls significantly reduces kernel
    /// crossings and system CPU usage in tight reactor loops.
    /// </para>
    /// </summary>
    /// <param name="ring">The io_uring instance.</param>
    /// <param name="waitNr">
    /// Minimum number of CQEs to wait for (must be &gt;= 1).
    /// A value of 1 is ideal for low-latency reactors.
    /// </param>
    /// <returns>
    /// Number of SQEs submitted on success, or <c>-errno</c> on error.
    /// </returns>
    [DllImport("uringshim")]
    internal static extern int shim_submit_and_wait(io_uring* ring, uint waitNr);
    /// <summary>
    /// Flushes pending SQEs (liburing), submits them, then waits until at least <paramref name="waitNr"/>
    /// CQEs are available or the timeout elapses. On success, writes up to <paramref name="waitNr"/>
    /// CQE pointers into <paramref name="cqes"/>.
    /// </summary>
    /// <returns>
    /// On success: number of CQE pointers written into <paramref name="cqes"/> (typically 1..waitNr).
    /// On failure: -errno (e.g. -ETIME on timeout).
    /// </returns>
    [DllImport("uringshim")]
    internal static extern int shim_submit_and_wait_timeout(
        io_uring* ring,
        io_uring_cqe** cqes,
        uint waitNr,
        __kernel_timespec* ts);
    /// <summary>
    /// Performs a direct <c>io_uring_enter(2)</c> syscall on the given ring.
    ///
    /// <para>
    /// This is the lowest-level way to drive an io_uring instance and allows
    /// submitting SQEs and waiting for CQEs in a <b>single</b> kernel entry,
    /// optionally with a timeout.
    /// </para>
    ///
    /// <para>
    /// This wrapper is primarily intended for high-performance reactor loops
    /// that need precise control over:
    /// <list type="bullet">
    /// <item><description>how many SQEs to submit</description></item>
    /// <item><description>how many CQEs to wait for</description></item>
    /// <item><description>whether to block and for how long</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Most code should prefer <see cref="shim_submit_and_wait"/> for simplicity.
    /// Use this method only when a timeout or custom flags are required.
    /// </para>
    /// </summary>
    /// <param name="ring">The io_uring instance.</param>
    /// <param name="toSubmit">
    /// Number of SQEs to submit from the submission queue.
    /// Typically obtained via <see cref="shim_sq_ready"/>.
    /// </param>
    /// <param name="minComplete">
    /// Minimum number of CQEs to wait for before returning.
    /// Use <c>1</c> for event-driven reactors.
    /// </param>
    /// <param name="flags">
    /// Flags passed directly to <c>io_uring_enter</c>, e.g.
    /// <c>IORING_ENTER_GETEVENTS</c>.
    /// When using a timeout, <c>IORING_ENTER_EXT_ARG</c> is automatically added by the shim.
    /// </param>
    /// <param name="ts">
    /// Optional timeout (kernel timespec).
    /// Pass <c>null</c> for infinite wait.
    /// When non-null, the shim uses io_uring_enter2 with extended argument format.
    /// </param>
    /// <returns>
    /// On success, returns the number of submitted SQEs.
    /// On failure, returns <c>-errno</c> (e.g., <c>-EINTR</c>, <c>-ETIME</c>).
    /// </returns>
    [DllImport("uringshim")]
    internal static extern int shim_enter(
        io_uring* ring,
        uint toSubmit,
        uint minComplete,
        uint flags,
        __kernel_timespec* ts);
    /// <summary>
    /// Advances the completion queue head by <paramref name="count"/> entries,
    /// marking the previously peeked CQEs as consumed.
    ///
    /// <para>
    /// This should be used together with <see cref="shim_peek_batch_cqe"/> to
    /// acknowledge multiple CQEs at once instead of calling
    /// <see cref="shim_cqe_seen"/> for each individual CQE.
    /// </para>
    ///
    /// <para>
    /// Advancing the CQ head in bulk reduces cacheline contention and lowers
    /// per-CQE overhead in high-throughput workloads.
    /// </para>
    /// </summary>
    /// <param name="ring">The io_uring instance.</param>
    /// <param name="count">Number of CQEs to mark as seen.</param>
    [DllImport("uringshim")]
    internal static extern void shim_cq_advance(io_uring* ring, uint count);
    /// <summary>
    /// Returns the number of completion queue entries (CQEs) that are
    /// currently available to be consumed.
    ///
    /// <para>
    /// This is a non-blocking, userspace-only check and does <b>not</b>
    /// enter the kernel.
    /// </para>
    ///
    /// <para>
    /// Typically used to avoid unnecessary <see cref="shim_submit_and_wait"/>
    /// calls when CQEs are already present.
    /// </para>
    /// </summary>
    /// <param name="ring">The io_uring instance.</param>
    /// <returns>Number of ready CQEs in the completion queue.</returns>
    [DllImport("uringshim")]
    internal static extern uint shim_cq_ready(io_uring* ring);
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
    
    // =============================================================================
    // io_uring flags & constants (documented)
    // =============================================================================

    /// <summary>
    /// io_uring setup flag: enable I/O polling (busy polling) instead of interrupt-driven completions.
    /// <para>
    /// When enabled, the kernel actively polls the device for completion rather than relying on interrupts.
    /// This can reduce latency for very fast block devices (e.g., NVMe) at the cost of higher CPU usage.
    /// </para>
    /// <para>
    /// ⚠ Generally not a good fit for sockets/networking: it burns CPU and does not help typical NIC paths.
    /// </para>
    /// </summary>
    internal const uint IORING_SETUP_IOPOLL = 1u << 0;

    /// <summary>
    /// io_uring setup flag: enable Submission Queue polling (SQPOLL).
    /// <para>
    /// When enabled, the kernel creates a dedicated thread that continuously polls the submission queue (SQ)
    /// and submits requests on behalf of userspace. This reduces the number of <c>io_uring_enter</c> syscalls
    /// required for submission, which can improve throughput/latency at high submission rates.
    /// </para>
    /// <para>
    /// Tradeoff: a kernel thread stays alive and consumes CPU even when the workload is light.
    /// </para>
    /// </summary>
    internal const uint IORING_SETUP_SQPOLL = 1u << 1;

    /// <summary>
    /// io_uring setup flag: pin the SQPOLL kernel thread to a specific CPU.
    /// <para>
    /// Requires <see cref="IORING_SETUP_SQPOLL"/> and a valid CPU index provided at ring creation time.
    /// Pinning improves cache locality and avoids scheduler migrations for the SQPOLL thread.
    /// </para>
    /// </summary>
    internal const uint IORING_SETUP_SQ_AFF = 1u << 2;

    /// <summary>
    /// io_uring setup flag: request a completion queue (CQ) size different from the submission queue size.
    /// <para>
    /// Used together with native ring creation parameters to size CQ explicitly (useful when expecting more CQEs
    /// than SQEs, e.g., with multishot operations).
    /// </para>
    /// </summary>
    internal const uint IORING_SETUP_CQSIZE = 1u << 3;

    /// <summary>
    /// io_uring setup flag: clamp queue sizes to allowed limits instead of failing.
    /// <para>
    /// If the requested SQ/CQ sizes are outside what the kernel permits, the kernel may reduce (clamp) them
    /// to a supported value rather than returning an error.
    /// </para>
    /// </summary>
    internal const uint IORING_SETUP_CLAMP = 1u << 4;

    /// <summary>
    /// io_uring setup flag: optimize for a single submitting userspace thread.
    /// <para>
    /// The kernel can skip some synchronization/locking when it knows there is only one issuer of SQEs.
    /// This can reduce overhead slightly in tight reactor loops.
    /// </para>
    /// <para>
    /// ⚠ If you submit from multiple threads while this flag is set, behavior is undefined (can corrupt state).
    /// </para>
    /// </summary>
    internal const uint IORING_SETUP_SINGLE_ISSUER = 1u << 12;

    /// <summary>
    /// io_uring setup flag: defer task_work execution triggered by completions.
    /// <para>
    /// Some completions cause the kernel to schedule task_work. With this flag, the kernel defers that work and
    /// batches it, typically running it on a subsequent <c>io_uring_enter</c> call.
    /// </para>
    /// <para>
    /// Benefit: reduces latency spikes and can improve throughput under high completion rates.
    /// Tradeoff: some work is delayed, which can slightly increase tail latency for certain patterns.
    /// </para>
    /// </summary>
    internal const uint IORING_SETUP_DEFER_TASKRUN = 1u << 13;

    /// <summary>
    /// io_uring setup flag: do not use <c>mmap</c> for SQ/CQ rings.
    /// <para>
    /// Advanced / niche. Used when you want to avoid ring mmaps (e.g., for special memory management or sandboxing).
    /// Most applications should not use this unless they know exactly why.
    /// </para>
    /// </summary>
    internal const uint IORING_SETUP_NO_MMAP = 1u << 14; // 0x4000

    /// <summary>
    /// io_uring setup flag: only allow I/O on registered files (fixed files).
    /// <para>
    /// When set, operations generally must use registered file descriptors (via register_files) rather than arbitrary fds.
    /// Can reduce per-op overhead and can be used as a safety restriction.
    /// </para>
    /// </summary>
    internal const uint IORING_SETUP_REGISTERED_FD_ONLY = 1u << 15; // 0x8000


    // =============================================================================
    // CQE flags (cqe->flags)
    // =============================================================================

    /// <summary>
    /// CQE flag: a provided buffer (buf-ring / buffer selection) was used.
    /// <para>
    /// When set, the upper 16 bits of <c>cqe->flags</c> contain the selected buffer id (bid).
    /// </para>
    /// </summary>
    internal const uint IORING_CQE_F_BUFFER = 1u << 0;

    /// <summary>
    /// CQE flag: the operation will produce more completions (multishot).
    /// <para>
    /// When set, the request remains "active" and will keep generating CQEs over time
    /// without requiring a resubmit (until it is canceled or fails).
    /// </para>
    /// </summary>
    internal const uint IORING_CQE_F_MORE = 1u << 1;

    /// <summary>
    /// CQE flag: the kernel still owns this provided buffer and will use it for subsequent CQEs.
    /// <para>
    /// Set when incremental buffer consumption (<see cref="IOU_PBUF_RING_INC"/>) is enabled.
    /// The buffer must not be returned to the buf_ring until this flag is absent (the final CQE for this buffer).
    /// </para>
    /// <para>Requires kernel 6.12+.</para>
    /// </summary>
    internal const uint IORING_CQE_F_BUF_MORE = 1u << 4;

    /// <summary>
    /// Bit shift for extracting the buffer id (bid) from <c>cqe->flags</c>.
    /// <para>
    /// Buffer id is stored in bits 16..31 when <see cref="IORING_CQE_F_BUFFER"/> is set.
    /// Example: <c>bid = (ushort)(cqe.flags >> IORING_CQE_BUFFER_SHIFT)</c>.
    /// </para>
    /// </summary>
    internal const int IORING_CQE_BUFFER_SHIFT = 16;


    // =============================================================================
    // Cancel flags (prep_cancel / async cancel)
    // =============================================================================

    /// <summary>
    /// Cancel flag: cancel all matching requests (not just one).
    /// <para>
    /// Commonly used with cancel-by-user_data to stop multishot ops or multiple in-flight
    /// operations sharing the same match key.
    /// </para>
    /// </summary>
    internal const int IORING_ASYNC_CANCEL_ALL = 1 << 0;


    // =============================================================================
    // io_uring_enter(2) flags
    // =============================================================================

    /// <summary>
    /// io_uring_enter flag: request that the kernel waits for completions if none are ready.
    /// <para>
    /// Typically used for "submit + wait" patterns. Without this flag, enter may submit but not block for CQEs.
    /// </para>
    /// </summary>
    internal const uint IORING_ENTER_GETEVENTS = 1u << 0;

    /// <summary>
    /// io_uring_enter flag: use the extended argument format (enter2).
    /// <para>
    /// Required for certain advanced enter behaviors (including some timeout/argument passing modes).
    /// In your shim, you may automatically OR this flag when passing a timeout/extended args.
    /// </para>
    /// </summary>
    internal const uint IORING_ENTER_EXT_ARG = 1u << 3;


    // =============================================================================
    // Provided buffer ring flags (io_uring_buf_reg)
    // =============================================================================

    /// <summary>
    /// Buffer ring setup flag: enable incremental buffer consumption.
    /// <para>
    /// When set, the kernel can partially consume provided buffers across multiple recv CQEs
    /// instead of consuming one entire buffer per CQE. Each successive CQE writes data at an
    /// increasing offset within the same buffer. The CQE flag <see cref="IORING_CQE_F_BUF_MORE"/>
    /// indicates the kernel still owns the buffer; its absence marks the final CQE for that buffer.
    /// </para>
    /// <para>Requires kernel 6.12+.</para>
    /// </summary>
    internal const uint IOU_PBUF_RING_INC = 2;
}