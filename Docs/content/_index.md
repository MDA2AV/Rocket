---
title: zerg
layout: hextra-home
---

{{< hextra/hero-badge link="https://www.nuget.org/packages/zerg" >}}
  NuGet v0.4.11
{{< /hextra/hero-badge >}}

<div style="margin-top: 1.5rem; margin-bottom: 1.25rem;">
{{< hextra/hero-headline >}}
  High-Performance io_uring Networking for C#
{{< /hextra/hero-headline >}}
</div>

<div style="margin-bottom: 2rem;">
{{< hextra/hero-subtitle >}}
  A zero-allocation, reactor-pattern TCP server framework&nbsp;<br class="sm:hx-block hx-hidden" />built on Linux io_uring with multishot accept, multishot recv, and provided buffers.
{{< /hextra/hero-subtitle >}}
</div>

<div style="margin-bottom: 1.5rem;">
{{< hextra/hero-button text="Get Started" link="docs/getting-started/installation/" >}}
</div>

<div style="margin-top: 2rem;"></div>

{{< hextra/feature-grid >}}
  {{< hextra/feature-card
    title="io_uring Native"
    subtitle="Built directly on Linux io_uring via a thin C shim — no managed sockets, no epoll. Leverages the latest kernel features for maximum throughput with minimal syscalls."
    link="docs/architecture/io-uring/"
  >}}
  {{< hextra/feature-card
    title="Zero-Allocation Hot Path"
    subtitle="Unmanaged memory slabs, ValueTask-based async, lock-free SPSC/MPSC queues, and object pooling eliminate GC pauses on the critical path."
    link="docs/guides/zero-allocation/"
  >}}
  {{< hextra/feature-card
    title="Reactor Pattern"
    subtitle="One acceptor thread distributes connections round-robin across N reactor threads. Each reactor owns its own io_uring instance and connection map with zero contention."
    link="docs/architecture/reactor-pattern/"
  >}}
  {{< hextra/feature-card
    title="Scalable"
    subtitle="Scale from a single reactor to dozens. Each reactor independently manages thousands of concurrent connections with configurable buffer rings and CQE batching."
    link="docs/getting-started/configuration/"
  >}}
  {{< hextra/feature-card
    title="Flexible API"
    subtitle="High-level ReadOnlySequence APIs for easy parsing, low-level RingItem access for maximum control, IBufferWriter for pipelined writes, a zero-copy PipeReader adapter, and a Stream adapter for BCL compatibility."
    link="docs/api-reference/"
  >}}
  {{< hextra/feature-card
    title="Production Ready"
    subtitle="AOT-compatible, ships bundled native libraries for glibc and musl, and is available on NuGet."
    link="docs/getting-started/installation/"
  >}}
{{< /hextra/feature-grid >}}

<div class="uring-features-section">
  <h2 class="uring-features-title">io_uring Features</h2>
  <p class="uring-features-subtitle">Every connection is driven entirely through io_uring — no epoll, no managed sockets.</p>
  <div class="uring-features-grid">
    <div class="uring-feature-item">
      <div class="uring-feature-name">Multishot accept<span class="uring-tag">5.19+</span></div>
      <div class="uring-feature-desc">Accept connections continuously from a single SQE — no re-arming after each accept</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">Multishot recv<span class="uring-tag">6.0+</span></div>
      <div class="uring-feature-desc">Receive data continuously on a socket — one submission serves the entire connection lifetime</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">Provided buffer rings<span class="uring-tag">5.19+</span></div>
      <div class="uring-feature-desc">Kernel-managed buffer selection via <code>io_uring_buf_ring</code> — zero-copy recv into pre-registered memory slabs</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">Incremental buffer consumption<span class="uring-tag">6.12+</span></div>
      <div class="uring-feature-desc">The kernel packs multiple recvs into a single buffer at successive offsets, reducing buffer ring pressure</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">SQPOLL<span class="uring-tag">5.1+</span></div>
      <div class="uring-feature-desc">Dedicated kernel thread polls the submission queue, eliminating <code>io_uring_enter</code> syscalls on the submit path</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">SQ_AFF<span class="uring-tag">5.1+</span></div>
      <div class="uring-feature-desc">Pin the SQPOLL kernel thread to a specific CPU for cache locality</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">SINGLE_ISSUER<span class="uring-tag-pair"><span class="uring-tag">6.0+</span><span class="uring-tag uring-tag-accent">default</span></span></div>
      <div class="uring-feature-desc">Only one thread submits SQEs — the kernel skips internal locking</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">DEFER_TASKRUN<span class="uring-tag-pair"><span class="uring-tag">6.1+</span><span class="uring-tag uring-tag-accent">default</span></span></div>
      <div class="uring-feature-desc">Defer and batch kernel task_work to reduce latency spikes under high completion rates</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">Batch CQE processing<span class="uring-tag">5.1+</span></div>
      <div class="uring-feature-desc">Drain up to 4096 CQEs per loop iteration via <code>peek_batch_cqe</code> + <code>cq_advance</code></div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">Submit-and-wait<span class="uring-tag">5.1+</span></div>
      <div class="uring-feature-desc">Combined submit + wait in a single <code>io_uring_enter</code> syscall — halves kernel crossings</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">Direct io_uring_enter<span class="uring-tag">5.1+</span></div>
      <div class="uring-feature-desc">Low-level enter with timeout support via extended arguments for precise reactor control</div>
    </div>
    <div class="uring-feature-item">
      <div class="uring-feature-name">Async cancellation<span class="uring-tag">5.5+</span></div>
      <div class="uring-feature-desc">Cancel in-flight multishot operations by <code>user_data</code> match when connections close</div>
    </div>
  </div>
</div>
