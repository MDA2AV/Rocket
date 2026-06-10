// uringshim.h
#pragma once

#include <stdint.h>
#include <liburing.h>

#ifdef __cplusplus
extern "C" {
#endif

// -----------------------------------------------------------------------------
// Ring lifecycle / configuration
// -----------------------------------------------------------------------------

unsigned shim_get_ring_flags(struct io_uring* ring);

struct io_uring* shim_create_ring_ex(unsigned entries,
                                     unsigned flags,
                                     int      sq_thread_cpu,
                                     unsigned sq_thread_idle_ms,
                                     int*     err_out);

struct io_uring* shim_create_ring(unsigned entries, int* err_out);

void shim_destroy_ring(struct io_uring* ring);

// -----------------------------------------------------------------------------
// Core ring ops (SQ/CQ)
// -----------------------------------------------------------------------------

int      shim_submit(struct io_uring* ring);
int      shim_wait_cqe(struct io_uring* ring, struct io_uring_cqe** cqe);
int      shim_peek_batch_cqe(struct io_uring* ring, struct io_uring_cqe** cqes, unsigned count);
void     shim_cqe_seen(struct io_uring* ring, struct io_uring_cqe* cqe);
unsigned shim_sq_ready(struct io_uring* ring);
struct io_uring_sqe* shim_get_sqe(struct io_uring* ring);

// Timeout / wait variants
int shim_wait_cqe_timeout(struct io_uring* ring,
                          struct io_uring_cqe** cqe_ptr,
                          struct __kernel_timespec* ts);

int shim_wait_cqes(struct io_uring* ring,
                   struct io_uring_cqe** cqe_ptr,
                   unsigned wait_nr,
                   struct __kernel_timespec* ts);

int shim_wait_cqe_timeout_in(struct io_uring* ring,
                             struct io_uring_cqe** cqe,
                             long timeout_ms);

// Submit+wait helpers
int  shim_submit_and_wait(struct io_uring* ring, unsigned wait_nr);
void shim_cq_advance(struct io_uring* ring, unsigned count);
unsigned shim_cq_ready(struct io_uring* ring);

int shim_submit_and_wait_timeout(struct io_uring* ring,
                                 struct io_uring_cqe** cqes,
                                 unsigned int wait_nr,
                                 struct __kernel_timespec* ts);

// -----------------------------------------------------------------------------
// Multishot ops
// -----------------------------------------------------------------------------

void shim_prep_multishot_accept(struct io_uring_sqe* sqe, int lfd, int flags);

void shim_prep_recv_multishot_select(struct io_uring_sqe* sqe,
                                     int fd,
                                     unsigned buf_group,
                                     int flags);

// -----------------------------------------------------------------------------
// User-data helpers
// -----------------------------------------------------------------------------

void               shim_sqe_set_data64(struct io_uring_sqe* sqe, unsigned long long data);
unsigned long long shim_cqe_get_data64(const struct io_uring_cqe* cqe);

// -----------------------------------------------------------------------------
// Buf-ring helpers
// -----------------------------------------------------------------------------

struct io_uring_buf_ring* shim_setup_buf_ring(struct io_uring* ring,
                                              unsigned entries,
                                              unsigned bgid,
                                              unsigned flags,
                                              int* ret_out);

void shim_free_buf_ring(struct io_uring* ring,
                        struct io_uring_buf_ring* br,
                        unsigned entries,
                        unsigned bgid);

void shim_buf_ring_add(struct io_uring_buf_ring* br,
                       void* addr,
                       unsigned len,
                       unsigned short bid,
                       unsigned short mask,
                       unsigned idx);

void shim_buf_ring_advance(struct io_uring_buf_ring* br, unsigned count);

// -----------------------------------------------------------------------------
// CQE buffer helpers
// -----------------------------------------------------------------------------

int      shim_cqe_has_buffer(const struct io_uring_cqe* cqe);
unsigned shim_cqe_buffer_id(const struct io_uring_cqe* cqe);

// -----------------------------------------------------------------------------
// Send / cancel
// -----------------------------------------------------------------------------

void shim_prep_send(struct io_uring_sqe* sqe,
                    int fd,
                    const void* buf,
                    unsigned nbytes,
                    int flags);

void shim_prep_cancel64(struct io_uring_sqe* sqe,
                        unsigned long long user_data,
                        int flags);

// -----------------------------------------------------------------------------
// Direct enter wrappers 
// -----------------------------------------------------------------------------

int shim_enter2(struct io_uring* ring,
                unsigned to_submit,
                unsigned min_complete,
                unsigned flags,
                struct __kernel_timespec* ts);

int shim_enter4(struct io_uring* ring,
                unsigned to_submit,
                unsigned min_complete,
                unsigned flags,
                struct __kernel_timespec* ts);

int shim_enter(struct io_uring* ring,
               unsigned to_submit,
               unsigned min_complete,
               unsigned flags,
               struct __kernel_timespec* ts);

#ifdef __cplusplus
}
#endif

