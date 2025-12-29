#define _GNU_SOURCE
#include <stdlib.h>
#include <string.h>
#include <liburing.h>

//gcc -O2 -fPIC -shared -o liburingshim.so uringshim.c -luring

// This C shim wraps liburing with a stable C ABI for P/Invoke.
// We keep it intentionally thin: prepare SQEs, submit, get CQEs,
// set/get user-data, and handle buf-rings + multishot ops.
//
// IMPORTANT: This assumes a recent liburing and kernel with support for:
//   - io_uring_prep_multishot_accept
//   - io_uring_prep_recv_multishot + IOSQE_BUFFER_SELECT
//   - io_uring_setup_buf_ring / io_uring_free_buf_ring
//
// Error handling: liburing returns negative error codes (e.g., -EINVAL).
// We pass those directly back to managed code.
//

/**
 * Returns the ring's setup flags (IORING_SETUP_* bits) as stored in struct io_uring.
 * This lets managed code check whether SQPOLL / SQ_AFF is actually active.
 */
unsigned shim_get_ring_flags(struct io_uring* ring)
{
    if (!ring) return 0;
    return ring->flags;
}

/* -------- Ring lifecycle (simple queue_init path) -------- */

/**
 * Extended creator: allows passing io_uring_setup flags + SQPOLL tuning.
 *
 *  entries           : ring size
 *  flags             : IORING_SETUP_* flags (e.g. IORING_SETUP_SQPOLL | IORING_SETUP_SQ_AFF)
 *  sq_thread_cpu     : CPU to pin SQPOLL thread to (used only if IORING_SETUP_SQ_AFF is set).
 *                      Pass -1 to let the kernel choose.
 *  sq_thread_idle_ms : SQPOLL idle timeout in milliseconds (used only if IORING_SETUP_SQPOLL is set).
 *
 * Returns heap-allocated struct io_uring* or NULL on error.
 * On failure, *err_out contains -errno from io_uring_queue_init_params or -ENOMEM.
 */
struct io_uring* shim_create_ring_ex(unsigned entries,
                                     unsigned flags,
                                     int      sq_thread_cpu,
                                     unsigned sq_thread_idle_ms,
                                     int*     err_out)
{
    struct io_uring* ring = (struct io_uring*)malloc(sizeof(struct io_uring));
    if (!ring)
    {
        if (err_out) *err_out = -12; // -ENOMEM
        return NULL;
    }

    memset(ring, 0, sizeof(*ring));

    struct io_uring_params p;
    memset(&p, 0, sizeof(p));

    p.flags = flags;

    if (flags & IORING_SETUP_SQPOLL)
    {
        // io_uring expects idle in milliseconds
        p.sq_thread_idle = sq_thread_idle_ms;

        if ((flags & IORING_SETUP_SQ_AFF) && sq_thread_cpu >= 0)
        {
            p.sq_thread_cpu = sq_thread_cpu;
        }
    }

    int rc = io_uring_queue_init_params(entries, ring, &p);
    if (rc < 0)
    {
        free(ring);
        if (err_out) *err_out = rc;
        return NULL;
    }

    if (err_out) *err_out = 0;
    return ring;
}

/**
 * Allocates a struct io_uring and initializes a queue with 'entries'.
 * No special flags (e.g., no SQPOLL) are used here.
 * On failure, returns NULL and sets *err_out to a negative errno.
 */
struct io_uring* shim_create_ring(unsigned entries, int* err_out)
{
    struct io_uring* ring = (struct io_uring*)malloc(sizeof(struct io_uring));
    if (!ring)
    {
        if (err_out) *err_out = -12; // -ENOMEM
        return NULL;
    }

    memset(ring, 0, sizeof(*ring));

    int rc = io_uring_queue_init(entries, ring, 0); // defaults, single issuer from this thread
    if (rc < 0)
    {
        free(ring);
        if (err_out) *err_out = rc;
        return NULL;
    }

    if (err_out) *err_out = 0;
    return ring;
}

/**
 * Tears down the queue and frees the heap-allocated 'ring'.
 * Safe to call with NULL.
 */
void shim_destroy_ring(struct io_uring* ring)
{
    if (!ring) return;
    io_uring_queue_exit(ring);
    free(ring);
}

/* -------- Core ring ops -------- */

/** Submits pending SQEs to the kernel. Returns number submitted or -errno. */
int shim_submit(struct io_uring* ring)
{
    return io_uring_submit(ring);
}

/** Blocks until at least one CQE is available, returning 0 or -errno. */
int shim_wait_cqe(struct io_uring* ring, struct io_uring_cqe** cqe)
{
    return io_uring_wait_cqe(ring, cqe);
}

/**
 * Peeks up to 'count' CQEs without blocking.
 * Returns number of CQEs copied to 'cqes' (0..count).
 */
int shim_peek_batch_cqe(struct io_uring* ring, struct io_uring_cqe** cqes, unsigned count)
{
    return io_uring_peek_batch_cqe(ring, cqes, count);
}

/** Marks a CQE as seen so the kernel can reuse that slot. */
void shim_cqe_seen(struct io_uring* ring, struct io_uring_cqe* cqe)
{
    io_uring_cqe_seen(ring, cqe);
}

/** Returns number of ready (not yet submitted) SQEs in the SQ. */
unsigned shim_sq_ready(struct io_uring* ring)
{
    return io_uring_sq_ready(ring);
}

/** Returns a pointer to a free SQE or NULL if the SQ is full. */
struct io_uring_sqe* shim_get_sqe(struct io_uring* ring)
{
    return io_uring_get_sqe(ring);
}

/* -------- Multishot ops -------- */

/**
 * Prepares a multishot accept SQE.
 * Each completion corresponds to one accepted connection until the kernel stops.
 * 'flags' typically include SOCK_NONBLOCK so accepted fds are non-blocking.
 */
void shim_prep_multishot_accept(struct io_uring_sqe* sqe, int lfd, int flags)
{
    io_uring_prep_multishot_accept(sqe, lfd, NULL, NULL, flags);
}

/**
 * Prepares a multishot recv SQE that selects buffers from a registered
 * buf-ring (BUFFER_SELECT). The kernel will repeatedly produce CQEs as data
 * arrives, until it decides to stop (e.g., error/EOF).
 */
void shim_prep_recv_multishot_select(struct io_uring_sqe* sqe, int fd, unsigned buf_group, int flags)
{
    io_uring_prep_recv_multishot(sqe, fd, NULL, 0, flags);
    sqe->flags |= IOSQE_BUFFER_SELECT; // instruct kernel to pick buffers from buf-ring
    sqe->buf_group = buf_group;        // must match the bgid used in setup_buf_ring
}

/* -------- User-data helpers -------- */

/**
 * Stores a 64-bit opaque value in the SQE user-data field, later retrievable
 * from the corresponding CQE. Managed side encodes (kind, fd) into this.
 */
void shim_sqe_set_data64(struct io_uring_sqe* sqe, unsigned long long data)
{
    io_uring_sqe_set_data64(sqe, data);
}

/** Retrieves the 64-bit user-data from a CQE. */
unsigned long long shim_cqe_get_data64(const struct io_uring_cqe* cqe)
{
    return io_uring_cqe_get_data64(cqe);
}

/* -------- Buf-ring helpers -------- */

/**
 * Allocates/registers a buf-ring with 'entries' slots under buffer-group 'bgid'.
 * On success, returns the buf-ring pointer and sets *ret_out to 0.
 * On failure, returns NULL and sets *ret_out to -errno.
 *
 * After creation, the application should populate entries with io_uring_buf_ring_add()
 * and then call io_uring_buf_ring_advance() to publish them.
 */
struct io_uring_buf_ring* shim_setup_buf_ring(struct io_uring* ring,
                                              unsigned entries,
                                              unsigned bgid,
                                              unsigned flags,
                                              int* ret_out)
{
    return io_uring_setup_buf_ring(ring, entries, (int)bgid, flags, ret_out);
}

/**
 * Frees/unregisters a previously created buf-ring. It is the caller’s
 * responsibility to ensure no in-flight operations still reference it.
 */
void shim_free_buf_ring(struct io_uring* ring,
                        struct io_uring_buf_ring* br,
                        unsigned entries,
                        unsigned bgid)
{
    io_uring_free_buf_ring(ring, br, entries, (int)bgid);
}

/**
 * Stages a buffer [addr,len] with application-defined buffer id 'bid' into the
 * local producer view of the ring at logical 'idx'. The addition is not visible
 * to the kernel until shim_buf_ring_advance() is called.
 *
 * 'mask' must be (entries - 1) when entries is a power-of-two, used to wrap idx.
 */
void shim_buf_ring_add(struct io_uring_buf_ring* br,
                       void* addr,
                       unsigned len,
                       unsigned short bid,
                       unsigned short mask,
                       unsigned idx)
{
    io_uring_buf_ring_add(br, addr, len, bid, mask, idx);
}

/**
 * Publishes 'count' previously added buffers to the kernel (single producer).
 * For batching efficiency, add many buffers, then advance once.
 */
void shim_buf_ring_advance(struct io_uring_buf_ring* br, unsigned count)
{
    io_uring_buf_ring_advance(br, count);
}

/* -------- CQE buffer helpers -------- */

/** Returns non-zero if the CQE refers to a buffer selected from the buf-ring. */
int shim_cqe_has_buffer(const struct io_uring_cqe* cqe)
{
    return (cqe->flags & IORING_CQE_F_BUFFER) != 0;
}

/** Extracts the buffer id (bid) that the kernel used for this CQE. */
unsigned shim_cqe_buffer_id(const struct io_uring_cqe* cqe)
{
    return cqe->flags >> IORING_CQE_BUFFER_SHIFT;
}

/* -------- Send -------- */

/**
 * Prepares a send() SQE of 'nbytes' from 'buf' to socket 'fd'.
 * 'flags' is passed directly to send(2) (e.g., MSG_MORE if you ever need it).
 */
void shim_prep_send(struct io_uring_sqe* sqe,
                    int fd,
                    const void* buf,
                    unsigned nbytes,
                    int flags)
{
    io_uring_prep_send(sqe, fd, buf, nbytes, flags);
}

int shim_wait_cqe_timeout(struct io_uring *ring, struct io_uring_cqe **cqe_ptr, struct __kernel_timespec *ts) {
    return io_uring_wait_cqe_timeout(ring, cqe_ptr, ts);
}

/**
 * Blocks until at least one CQE is available, or until timeout,
 * or until 'wait_nr' CQEs are available (whichever comes first).
 *
 * Returns 0 on success, or -errno on error/timeout.
 *
 * Note: we pass NULL for sigmask, so no signal mask is applied.
 */
int shim_wait_cqes(struct io_uring *ring,
                   struct io_uring_cqe **cqe_ptr,
                   unsigned wait_nr,
                   struct __kernel_timespec *ts)
{
    return io_uring_wait_cqes(ring, cqe_ptr, wait_nr, ts, NULL);
}

/**
 * Blocks until at least one CQE is available or the timeout elapses.
 *
 * @param ring   The io_uring instance.
 * @param cqe    Out: receives the pointer to the CQE when successful.
 * @param timeout_ms  Timeout in milliseconds (>= 0).
 *
 * @return 0 on success, -ETIME on timeout, or -errno on error.
 */
int shim_wait_cqe_timeout_in(struct io_uring* ring,
                          struct io_uring_cqe** cqe,
                          long timeout_ms)
{
    struct __kernel_timespec ts;
    ts.tv_sec  = timeout_ms / 1000;
    ts.tv_nsec = (timeout_ms % 1000) * 1000000LL; // ms → ns

    return io_uring_wait_cqe_timeout(ring, cqe, &ts);
}