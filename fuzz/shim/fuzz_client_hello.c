/*
 * Fuzz iq_count_host_names - the one place ioxide parses attacker-chosen bytes by hand.
 *
 * The function walks a raw ClientHello counting server_name entries, four levels of
 * length-prefixed structure deep, and every one of those lengths comes from the peer. What is
 * being looked for is the usual shape of a hand-written parser: a read past the end, a length that
 * wraps, a loop that does not advance. ASan and UBSan are the oracle for the first two; the third
 * shows up as a hang.
 *
 * There is one behavioural invariant worth asserting beyond "did not crash", and it is checked
 * below: a non-negative answer is a COUNT OF ENTRIES, and each entry costs at least three bytes on
 * the wire, so a count can never exceed len/3. A parser that failed to advance would report a
 * number far above that long before it read out of bounds.
 *
 * The function itself is extracted from the shim by build.sh - not copied - so this harness cannot
 * drift from what ships.
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "iq_count_host_names.inc"

/* How many inputs got far enough to be judged rather than rejected at a length check. A sweep
 * where this stays near zero is not fuzzing the parser, it is fuzzing its front door - which is
 * the usual way a hand-rolled fuzzer flatters itself. */
static unsigned long long g_reached;

static void check(const uint8_t *data, size_t len)
{
    int n = iq_count_host_names(data, len);

    if (n >= 0) {
        g_reached++;
    }

    if (n >= 0 && (size_t)n > len / 3u + 1u) {
        fprintf(stderr, "count %d is impossible for %zu bytes: an entry costs at least 3\n", n, len);
        abort();
    }
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t len)
{
    check(data, len);
    return 0;
}

#ifdef IQ_FUZZ_STANDALONE

/* A valid ClientHello carrying one server_name, as the skeleton to mutate. Purely random bytes are
 * rejected by the first length check essentially always, so a sweep built on them would exercise
 * the header and never reach the extension loop this is actually about. */
static size_t skeleton(uint8_t *out, size_t cap)
{
    static const uint8_t host[] = "alpha.test";
    size_t sni_entry = 1 + 2 + sizeof(host) - 1;      /* type, length, name */
    size_t sni_ext   = 2 + sni_entry;                 /* list length, then the entry */
    size_t ext_block = 4 + sni_ext;                   /* extension type, length, body */
    size_t total     = 4 + 2 + 32 + 1 + 2 + 2 + 1 + 2 + ext_block;
    size_t o = 0;

    if (cap < total) {
        return 0;
    }

    out[o++] = 0x01; out[o++] = 0; out[o++] = 0; out[o++] = 0;   /* handshake header */
    out[o++] = 0x03; out[o++] = 0x03;                            /* legacy_version */
    memset(out + o, 0xAB, 32); o += 32;                          /* random */
    out[o++] = 0x00;                                             /* legacy_session_id: empty */
    out[o++] = 0x00; out[o++] = 0x02;                            /* cipher_suites: 2 bytes */
    out[o++] = 0x13; out[o++] = 0x01;
    out[o++] = 0x01; out[o++] = 0x00;                            /* compression_methods */

    out[o++] = (uint8_t)(ext_block >> 8); out[o++] = (uint8_t)ext_block;
    out[o++] = 0x00; out[o++] = 0x00;                            /* extension type 0: server_name */
    out[o++] = (uint8_t)(sni_ext >> 8);   out[o++] = (uint8_t)sni_ext;
    out[o++] = (uint8_t)(sni_entry >> 8); out[o++] = (uint8_t)sni_entry;
    out[o++] = 0x00;                                             /* name type: host_name */
    out[o++] = 0x00; out[o++] = (uint8_t)(sizeof(host) - 1);
    memcpy(out + o, host, sizeof(host) - 1); o += sizeof(host) - 1;

    return o;
}

/* xorshift64*, so a failing run is reproducible from its seed alone. */
/* 4 handshake header + 2 legacy_version + 32 random: read past, never read INTO. */
#define INERT_END 38u

static uint64_t rng_state = 0x9E3779B97F4A7C15ull;
static uint64_t rng(void)
{
    rng_state ^= rng_state >> 12;
    rng_state ^= rng_state << 25;
    rng_state ^= rng_state >> 27;
    return rng_state * 0x2545F4914F6CDD1Dull;
}

static void replay_corpus(const char *dir)
{
    char path[512];
    uint8_t buf[4096];
    int found = 0;

    for (int i = 0; i < 64; i++) {
        snprintf(path, sizeof(path), "%s/seed-%02d.bin", dir, i);
        FILE *f = fopen(path, "rb");
        if (f == NULL) {
            continue;
        }
        size_t n = fread(buf, 1, sizeof(buf), f);
        fclose(f);
        check(buf, n);
        found++;
    }
    printf("corpus: replayed %d seed(s) from %s\n", found, dir);
}

int main(int argc, char **argv)
{
    long iterations = (argc > 1) ? strtol(argv[1], NULL, 10) : 2000000;
    if (argc > 2) {
        rng_state = strtoull(argv[2], NULL, 10);
    }

    replay_corpus("fuzz/shim/corpus");
    printf("sweep: %ld iterations, seed %llu\n", iterations, (unsigned long long)rng_state);

    uint8_t buf[512];
    size_t base = skeleton(buf, sizeof(buf));
    if (base == 0) {
        fprintf(stderr, "skeleton did not fit\n");
        return 1;
    }
    check(buf, base);   /* the unmutated one must parse */

    uint8_t work[512];
    for (long i = 0; i < iterations; i++) {
        size_t len = base;
        memcpy(work, buf, len);

        /* Mutate the LENGTH fields hardest - they are what the parser trusts. Bytes 6..37 are the
         * ClientHello random, which the parser skips over without reading, so a uniform mutation
         * wastes ~48% of its edits on a field that cannot change the outcome. Most edits therefore
         * land at or after the first length prefix; a few stay uniform so the fixed header is not
         * left entirely unprobed. */
        int edits = (int)(rng() % 6u) + 1;
        for (int e = 0; e < edits; e++) {
            size_t at = (rng() % 5u == 0 || len <= INERT_END)
                ? rng() % len
                : INERT_END + rng() % (len - INERT_END);
            work[at] = (uint8_t)rng();
        }
        if (rng() % 3u == 0) {
            len = (size_t)(rng() % (base + 1));
        }

        check(work, len);
    }

    printf("ok: no crash, no impossible count\n");
    printf("    %llu of %ld inputs parsed far enough to be counted (%.1f%%)\n",
           g_reached, iterations, iterations ? 100.0 * (double)g_reached / (double)iterations : 0.0);
    return 0;
}

#endif
