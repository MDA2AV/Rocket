/*
 * ioxide.ngtcp2 native shim: a thin, C#-friendly facade over ngtcp2 + its picotls crypto backend.
 *
 * Why it exists: ngtcp2's conn API takes large versioned structs (settings, transport params,
 * ~36-entry callback tables) and picotls' context is full of C bitfields - marshaling those from
 * C# would be fragile against upstream layout drift. The shim owns every struct layout in C
 * (compiled against the exact bundled headers) and exposes a small stable ABI:
 *
 *   engine  = iq_engine_new(cert, key, cidlen, alpn, cbs) one per factory; owns ptls_context
 *   conn    = iq_accept(engine, addrs, first_pkt, ...)   validates + creates the server conn
 *             iq_conn_read(...)                          feed one UDP datagram
 *             iq_conn_write(...)                         produce one UDP datagram (loop until 0)
 *             iq_conn_open_uni / iq_conn_get_alpn        H3 plumbing (uni streams, negotiated proto)
 *             iq_conn_expiry / iq_conn_handle_expiry     ns-precision engine deadlines
 *             iq_conn_free / iq_engine_free
 *
 * Events flow back to C# through the iq_callbacks function pointers; every call into and out of
 * the shim happens on the owning reactor thread.
 */
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#include <ngtcp2/ngtcp2.h>
#include <ngtcp2/ngtcp2_crypto.h>
#include <ngtcp2/ngtcp2_crypto_picotls.h>

#include <picotls.h>
#include <picotls/openssl.h>

#include <openssl/bio.h>
#include <openssl/pem.h>
#include <openssl/evp.h>

/* ---- callback table into C# ------------------------------------------------------------- */

typedef struct iq_callbacks {
    void (*on_stream_data)(void *user, int64_t stream_id, const uint8_t *data, size_t datalen, int fin);
    void (*on_stream_close)(void *user, int64_t stream_id, uint64_t app_error_code);
    void (*on_handshake_completed)(void *user);
    void (*on_new_cid)(void *user, const uint8_t *cid, size_t cidlen);
    void (*on_retire_cid)(void *user, const uint8_t *cid, size_t cidlen);
    /* Peer aborted its sending side (RESET_STREAM) / asked us to stop ours (STOP_SENDING).
     * Either may be NULL (the test client ignores them). */
    void (*on_stream_reset)(void *user, int64_t stream_id, uint64_t app_error_code);
    void (*on_stream_stop_sending)(void *user, int64_t stream_id, uint64_t app_error_code);
    /* Bytes [offset, offset+datalen) of the stream are acknowledged: the app may free them.
     * ngtcp2 retains POINTERS into the app's buffers for retransmission until this fires - the
     * caller of iq_conn_write must keep stream bytes alive until then. May be NULL. */
    void (*on_acked_stream_data)(void *user, int64_t stream_id, uint64_t offset, uint64_t datalen);
} iq_callbacks;

/* ---- objects ---------------------------------------------------------------------------- */

typedef struct iq_engine {
    ptls_context_t                  ptls_ctx;
    ptls_openssl_sign_certificate_t sign_cert;
    ptls_on_client_hello_t          on_client_hello;
    iq_callbacks                    cbs;
    size_t                          cidlen;
    uint8_t                         alpn[256];   /* allowlist, wire format (len-prefixed entries) */
    size_t                          alpn_len;    /* 0 = accept whatever the client offers */
} iq_engine;

typedef struct iq_conn {
    ngtcp2_conn                *conn;
    iq_engine                  *engine;    /* server connections only (CID length, ptls ctx) */
    iq_callbacks                cbs;       /* copied from whichever engine created this conn */
    ngtcp2_crypto_conn_ref      conn_ref;
    ngtcp2_crypto_picotls_ctx   cptls;
    ngtcp2_sockaddr_union       local_addr;
    ngtcp2_sockaddr_union       remote_addr;
    ngtcp2_path                 path;
    ngtcp2_ccerr                last_error;
    void                       *user;
    void (*on_stream_data_raw)(void *user, int64_t stream_id, const uint8_t *data, size_t datalen, int fin);
    ptls_raw_extension_t        exts[2];   /* [0] QUIC transport params (filled by ngtcp2), [1] terminator */
    char                        alpn[64];  /* client: the single protocol we offer */
    ptls_iovec_t                alpn_vec;  /* points into alpn[], handed to picotls for the CH */

    /* Streams whose receive window the APPLICATION paces: delivery skips the auto-credit below
     * and the app opens the window explicitly via iq_conn_consume as it consumes. Bounded set -
     * overflow degrades gracefully to auto-credit (no backpressure for the extra stream). */
    int64_t paced[32];
    int     paced_count;
} iq_conn;

static int iq_paced_index(iq_conn *c, int64_t stream_id)
{
    for (int i = 0; i < c->paced_count; i++) {
        if (c->paced[i] == stream_id) {
            return i;
        }
    }
    return -1;
}

/* ---- helpers ---------------------------------------------------------------------------- */

static ngtcp2_conn *iq_get_conn(ngtcp2_crypto_conn_ref *ref)
{
    return ((iq_conn *)ref->user_data)->conn;
}

static void iq_rand(uint8_t *dest, size_t destlen, const ngtcp2_rand_ctx *rand_ctx)
{
    (void)rand_ctx;
    ptls_openssl_random_bytes(dest, destlen);
}

/* ALPN. With an engine allowlist: pick the client's first offer that we accept, else fail the
 * handshake (RFC 9001 §8.1: no mutual protocol = no_application_protocol). Without one (empty
 * allowlist): accept whichever the client offered first - selection is the app's concern. */
static int iq_on_client_hello(ptls_on_client_hello_t *self, ptls_t *tls,
                              ptls_on_client_hello_parameters_t *params)
{
    iq_engine *e = (iq_engine *)((char *)self - offsetof(iq_engine, on_client_hello));

    if (params->negotiated_protocols.count == 0) {
        return e->alpn_len == 0 ? 0 : PTLS_ALERT_NO_APPLICATION_PROTOCOL;
    }

    if (e->alpn_len == 0) {
        return ptls_set_negotiated_protocol(
            tls,
            (const char *)params->negotiated_protocols.list[0].base,
            params->negotiated_protocols.list[0].len);
    }

    for (size_t i = 0; i < params->negotiated_protocols.count; i++) {
        ptls_iovec_t offer = params->negotiated_protocols.list[i];
        for (size_t off = 0; off < e->alpn_len;) {
            size_t len = e->alpn[off];
            if (len == offer.len && memcmp(e->alpn + off + 1, offer.base, len) == 0) {
                return ptls_set_negotiated_protocol(tls, (const char *)offer.base, offer.len);
            }
            off += 1 + len;
        }
    }
    return PTLS_ALERT_NO_APPLICATION_PROTOCOL;
}

/* ---- ngtcp2 callbacks ------------------------------------------------------------------- */

static int iq_cb_handshake_completed(ngtcp2_conn *conn, void *user_data)
{
    (void)conn;
    iq_conn *c = user_data;
    if (c->cbs.on_handshake_completed) c->cbs.on_handshake_completed(c->user);
    return 0;
}

static int iq_cb_recv_stream_data(ngtcp2_conn *conn, uint32_t flags, int64_t stream_id,
                                  uint64_t offset, const uint8_t *data, size_t datalen,
                                  void *user_data, void *stream_user_data)
{
    (void)offset; (void)stream_user_data;
    iq_conn *c = user_data;

    /* Consume connection + stream flow-control credit for what we just took - unless the
     * app paces this stream, in which case it credits via iq_conn_consume as it reads. */
    if (iq_paced_index(c, stream_id) < 0) {
        ngtcp2_conn_extend_max_offset(conn, datalen);
        ngtcp2_conn_extend_max_stream_offset(conn, stream_id, datalen);
    }

    if (c->cbs.on_stream_data) {
        c->cbs.on_stream_data(c->user, stream_id, data, datalen,
                              (flags & NGTCP2_STREAM_DATA_FLAG_FIN) != 0);
    } else if (c->on_stream_data_raw) {
        c->on_stream_data_raw(c->user, stream_id, data, datalen,
                              (flags & NGTCP2_STREAM_DATA_FLAG_FIN) != 0);
    }
    return 0;
}

static int iq_cb_stream_close(ngtcp2_conn *conn, uint32_t flags, int64_t stream_id,
                              uint64_t app_error_code, void *user_data, void *stream_user_data)
{
    (void)stream_user_data;
    iq_conn *c = user_data;
    if (!(flags & NGTCP2_STREAM_CLOSE_FLAG_APP_ERROR_CODE_SET)) {
        app_error_code = 0;
    }

    /* Replenish the peer's stream allowance: initial_max_streams_* is a WINDOW, not a lifetime
     * cap - without this a connection stalls for good after its first 100 requests. */
    if ((stream_id & 0x3) == 0x0) {
        ngtcp2_conn_extend_max_streams_bidi(conn, 1);
    } else if ((stream_id & 0x3) == 0x2) {
        ngtcp2_conn_extend_max_streams_uni(conn, 1);
    }

    int pi = iq_paced_index(c, stream_id);
    if (pi >= 0) {
        c->paced[pi] = c->paced[--c->paced_count];
    }

    if (c->cbs.on_stream_close) c->cbs.on_stream_close(c->user, stream_id, app_error_code);
    return 0;
}

static int iq_cb_stream_reset(ngtcp2_conn *conn, int64_t stream_id, uint64_t final_size,
                              uint64_t app_error_code, void *user_data, void *stream_user_data)
{
    (void)conn; (void)final_size; (void)stream_user_data;
    iq_conn *c = user_data;
    if (c->cbs.on_stream_reset) {
        c->cbs.on_stream_reset(c->user, stream_id, app_error_code);
    }
    return 0;
}

static int iq_cb_stream_stop_sending(ngtcp2_conn *conn, int64_t stream_id, uint64_t app_error_code,
                                     void *user_data, void *stream_user_data)
{
    (void)conn; (void)stream_user_data;
    iq_conn *c = user_data;
    if (c->cbs.on_stream_stop_sending) {
        c->cbs.on_stream_stop_sending(c->user, stream_id, app_error_code);
    }
    return 0;
}

static int iq_cb_acked_stream_data_offset(ngtcp2_conn *conn, int64_t stream_id, uint64_t offset,
                                          uint64_t datalen, void *user_data, void *stream_user_data)
{
    (void)conn; (void)stream_user_data;
    iq_conn *c = user_data;
    if (c->cbs.on_acked_stream_data) {
        c->cbs.on_acked_stream_data(c->user, stream_id, offset, datalen);
    }
    return 0;
}

static int iq_cb_get_new_connection_id(ngtcp2_conn *conn, ngtcp2_cid *cid, uint8_t *token,
                                       size_t cidlen, void *user_data)
{
    (void)conn;
    iq_conn *c = user_data;
    ptls_openssl_random_bytes(cid->data, cidlen);
    cid->datalen = cidlen;
    ptls_openssl_random_bytes(token, NGTCP2_STATELESS_RESET_TOKENLEN);
    if (c->cbs.on_new_cid) c->cbs.on_new_cid(c->user, cid->data, cid->datalen);
    return 0;
}

static int iq_cb_remove_connection_id(ngtcp2_conn *conn, const ngtcp2_cid *cid, void *user_data)
{
    (void)conn;
    iq_conn *c = user_data;
    if (c->cbs.on_retire_cid) c->cbs.on_retire_cid(c->user, cid->data, cid->datalen);
    return 0;
}

/* CID generator that doesn't report to a reactor (the test client has no engine->cbs). */
static int iq_cb_get_new_connection_id_noreport(ngtcp2_conn *conn, ngtcp2_cid *cid, uint8_t *token,
                                                size_t cidlen, void *user_data)
{
    (void)conn; (void)user_data;
    ptls_openssl_random_bytes(cid->data, cidlen);
    cid->datalen = cidlen;
    ptls_openssl_random_bytes(token, NGTCP2_STATELESS_RESET_TOKENLEN);
    return 0;
}

/* ---- engine ----------------------------------------------------------------------------- */

iq_engine *iq_engine_new(const char *cert_pem_path, const char *key_pem_path,
                         size_t cidlen, const uint8_t *alpn, size_t alpn_len,
                         iq_callbacks cbs)
{
    iq_engine *e = calloc(1, sizeof(*e));
    if (e == NULL) {
        return NULL;
    }
    e->cbs    = cbs;
    e->cidlen = cidlen;

    if (alpn != NULL && alpn_len > 0) {
        if (alpn_len > sizeof(e->alpn)) {
            free(e);
            return NULL;
        }
        memcpy(e->alpn, alpn, alpn_len);
        e->alpn_len = alpn_len;
    }

    e->ptls_ctx.random_bytes = ptls_openssl_random_bytes;
    e->ptls_ctx.get_time     = &ptls_get_time;
    e->ptls_ctx.key_exchanges = ptls_openssl_key_exchanges;
    e->ptls_ctx.cipher_suites = ptls_openssl_cipher_suites;

    e->on_client_hello.cb     = iq_on_client_hello;
    e->ptls_ctx.on_client_hello = &e->on_client_hello;

    if (ptls_load_certificates(&e->ptls_ctx, cert_pem_path) != 0) {
        fprintf(stderr, "[ioxide.ngtcp2] failed to load certificates from %s\n", cert_pem_path);
        goto fail;
    }

    {
        BIO *bio = BIO_new_file(key_pem_path, "r");
        if (bio == NULL) {
            fprintf(stderr, "[ioxide.ngtcp2] failed to open key %s\n", key_pem_path);
            goto fail;
        }
        EVP_PKEY *pkey = PEM_read_bio_PrivateKey(bio, NULL, NULL, NULL);
        BIO_free(bio);
        if (pkey == NULL) {
            fprintf(stderr, "[ioxide.ngtcp2] failed to parse key %s\n", key_pem_path);
            goto fail;
        }
        int rv = ptls_openssl_init_sign_certificate(&e->sign_cert, pkey);
        EVP_PKEY_free(pkey);
        if (rv != 0) {
            fprintf(stderr, "[ioxide.ngtcp2] failed to init sign_certificate\n");
            goto fail;
        }
        e->ptls_ctx.sign_certificate = &e->sign_cert.super;
    }

    if (ngtcp2_crypto_picotls_configure_server_context(&e->ptls_ctx) != 0) {
        fprintf(stderr, "[ioxide.ngtcp2] ngtcp2_crypto_picotls_configure_server_context failed\n");
        goto fail;
    }

    return e;

fail:
    free(e);
    return NULL;
}

void iq_engine_free(iq_engine *e)
{
    if (e == NULL) {
        return;
    }
    ptls_openssl_dispose_sign_certificate(&e->sign_cert);
    free(e);
}

const char *iq_version(void)
{
    return ngtcp2_version(0)->version_str;
}

/* ---- connection ------------------------------------------------------------------------- */

/* Validate the first datagram of a new connection and build the server conn for it.
 * scid_out receives the connection ID this server minted (engine->cidlen bytes) so the caller
 * can register the route. Returns NULL if the packet is not an acceptable Initial. */
iq_conn *iq_accept(iq_engine *e,
                   const void *local_sa, size_t local_salen,
                   const void *remote_sa, size_t remote_salen,
                   const uint8_t *pkt, size_t pktlen,
                   uint64_t ts, void *user, uint8_t *scid_out)
{
    ngtcp2_pkt_hd hd;
    if (ngtcp2_accept(&hd, pkt, pktlen) != 0) {
        return NULL;
    }

    iq_conn *c = calloc(1, sizeof(*c));
    if (c == NULL) {
        return NULL;
    }
    c->engine = e;
    c->cbs = e->cbs;
    c->user   = user;

    memcpy(&c->local_addr, local_sa, local_salen);
    memcpy(&c->remote_addr, remote_sa, remote_salen);
    c->path.local.addr     = &c->local_addr.sa;
    c->path.local.addrlen  = (ngtcp2_socklen)local_salen;
    c->path.remote.addr    = &c->remote_addr.sa;
    c->path.remote.addrlen = (ngtcp2_socklen)remote_salen;

    ngtcp2_callbacks callbacks = {0};
    callbacks.recv_client_initial       = ngtcp2_crypto_recv_client_initial_cb;
    callbacks.recv_crypto_data          = ngtcp2_crypto_recv_crypto_data_cb;
    callbacks.encrypt                   = ngtcp2_crypto_encrypt_cb;
    callbacks.decrypt                   = ngtcp2_crypto_decrypt_cb;
    callbacks.hp_mask                   = ngtcp2_crypto_hp_mask_cb;
    callbacks.update_key                = ngtcp2_crypto_update_key_cb;
    callbacks.delete_crypto_aead_ctx    = ngtcp2_crypto_delete_crypto_aead_ctx_cb;
    callbacks.delete_crypto_cipher_ctx  = ngtcp2_crypto_delete_crypto_cipher_ctx_cb;
    callbacks.get_path_challenge_data   = ngtcp2_crypto_get_path_challenge_data_cb;
    callbacks.version_negotiation       = ngtcp2_crypto_version_negotiation_cb;
    callbacks.rand                      = iq_rand;
    callbacks.get_new_connection_id     = iq_cb_get_new_connection_id;
    callbacks.remove_connection_id      = iq_cb_remove_connection_id;
    callbacks.handshake_completed       = iq_cb_handshake_completed;
    callbacks.recv_stream_data          = iq_cb_recv_stream_data;
    callbacks.stream_close              = iq_cb_stream_close;
    callbacks.stream_reset              = iq_cb_stream_reset;
    callbacks.stream_stop_sending       = iq_cb_stream_stop_sending;
    callbacks.acked_stream_data_offset  = iq_cb_acked_stream_data_offset;

    ngtcp2_settings settings;
    ngtcp2_settings_default(&settings);
    settings.initial_ts = ts;

    ngtcp2_transport_params params;
    ngtcp2_transport_params_default(&params);
    params.initial_max_stream_data_bidi_local  = 256 * 1024;
    params.initial_max_stream_data_bidi_remote = 256 * 1024;
    params.initial_max_stream_data_uni         = 256 * 1024;
    params.initial_max_data                    = 1024 * 1024;
    params.initial_max_streams_bidi            = 1024;
    params.initial_max_streams_uni             = 100;
    params.original_dcid                       = hd.dcid;
    params.original_dcid_present               = 1;

    ngtcp2_cid scid;
    scid.datalen = e->cidlen;
    ptls_openssl_random_bytes(scid.data, scid.datalen);

    if (ngtcp2_conn_server_new(&c->conn, &hd.scid, &scid, &c->path, hd.version,
                               &callbacks, &settings, &params, NULL, c) != 0) {
        free(c);
        return NULL;
    }

    c->conn_ref.get_conn  = iq_get_conn;
    c->conn_ref.user_data = c;

    ngtcp2_crypto_picotls_ctx_init(&c->cptls);
    c->cptls.ptls = ptls_new(&e->ptls_ctx, 1 /* server */);
    if (c->cptls.ptls == NULL) {
        ngtcp2_conn_del(c->conn);
        free(c);
        return NULL;
    }
    *ptls_get_data_ptr(c->cptls.ptls) = &c->conn_ref;

    c->exts[1].type = UINT16_MAX;   /* picotls terminator; [0] is filled during the handshake */
    c->cptls.handshake_properties.additional_extensions = c->exts;

    if (ngtcp2_crypto_picotls_configure_server_session(&c->cptls) != 0) {
        ptls_free(c->cptls.ptls);
        ngtcp2_conn_del(c->conn);
        free(c);
        return NULL;
    }

    ngtcp2_conn_set_tls_native_handle(c->conn, &c->cptls);
    ngtcp2_ccerr_default(&c->last_error);

    memcpy(scid_out, scid.data, scid.datalen);
    return c;
}

void iq_conn_free(iq_conn *c)
{
    if (c == NULL) {
        return;
    }
    if (c->cptls.ptls != NULL) {
        ngtcp2_crypto_picotls_deconfigure_session(&c->cptls);
        ptls_free(c->cptls.ptls);
    }
    ngtcp2_conn_del(c->conn);
    free(c);
}

/* Feed one UDP datagram (the transport already split GRO trains). Returns 0, or a negative
 * ngtcp2 error - NGTCP2_ERR_DRAINING / NGTCP2_ERR_DROP_CONN mean "stop using this conn". */
int iq_conn_read(iq_conn *c, const void *remote_sa, size_t remote_salen,
                 const uint8_t *pkt, size_t pktlen, uint8_t ecn, uint64_t ts)
{
    (void)remote_sa; (void)remote_salen;   /* milestone: no migration; path is fixed at accept */

    ngtcp2_pkt_info pi = { .ecn = (uint8_t)(ecn & 0x3) };
    return ngtcp2_conn_read_pkt(c->conn, &c->path, &pi, pkt, pktlen, ts);
}

/* Produce at most one UDP datagram into dest. With stream_id >= 0, tries to include data from
 * (data, datalen) on that stream; *pconsumed reports how much was accepted (-1 = none).
 * Returns the datagram size, 0 when there is nothing (more) to send, or a negative ngtcp2
 * error. NGTCP2_ERR_STREAM_DATA_BLOCKED / STREAM_SHUT_WR simply mean "stop sending app data".  */
ngtcp2_ssize iq_conn_write(iq_conn *c, uint8_t *dest, size_t destlen,
                           int64_t stream_id, const uint8_t *data, size_t datalen, int fin,
                           int64_t *pconsumed, uint64_t ts)
{
    ngtcp2_vec vec = { .base = (uint8_t *)data, .len = datalen };
    ngtcp2_ssize consumed = -1;

    uint32_t flags = 0;
    if (fin) {
        flags |= NGTCP2_WRITE_STREAM_FLAG_FIN;
    }

    ngtcp2_ssize n = ngtcp2_conn_writev_stream(
        c->conn, &c->path, NULL, dest, destlen, &consumed, flags, stream_id,
        data != NULL ? &vec : NULL, data != NULL ? 1 : 0, ts);

    *pconsumed = consumed;
    return n;
}

/* Build the CONNECTION_CLOSE datagram for the current error state (0 = nothing to send). */
ngtcp2_ssize iq_conn_write_close(iq_conn *c, uint8_t *dest, size_t destlen, uint64_t ts)
{
    return ngtcp2_conn_write_connection_close(c->conn, &c->path, NULL, dest, destlen,
                                              &c->last_error, ts);
}

/* App-initiated close: record an APPLICATION error (e.g. H3_NO_ERROR for graceful shutdown) and
 * build the CONNECTION_CLOSE datagram carrying it. The caller sends it and tears the conn down. */
ngtcp2_ssize iq_conn_close(iq_conn *c, uint64_t app_error_code,
                           uint8_t *dest, size_t destlen, uint64_t ts)
{
    ngtcp2_ccerr_set_application_error(&c->last_error, app_error_code, NULL, 0);
    return ngtcp2_conn_write_connection_close(c->conn, &c->path, NULL, dest, destlen,
                                              &c->last_error, ts);
}

uint64_t iq_conn_expiry(iq_conn *c)
{
    return ngtcp2_conn_get_expiry(c->conn);
}

int iq_conn_handle_expiry(iq_conn *c, uint64_t ts)
{
    return ngtcp2_conn_handle_expiry(c->conn, ts);
}

int iq_conn_is_established(iq_conn *c)
{
    return ngtcp2_conn_get_handshake_completed(c->conn);
}

int iq_conn_in_draining(iq_conn *c)
{
    return ngtcp2_conn_in_draining_period(c->conn);
}

/* Open a server-initiated unidirectional stream (H3 control / QPACK). Returns the stream id, or
 * a negative ngtcp2 error (e.g. STREAM_ID_BLOCKED when the peer's uni allowance is exhausted). */
int64_t iq_conn_open_uni(iq_conn *c)
{
    int64_t sid;
    int rv = ngtcp2_conn_open_uni_stream(c->conn, &sid, NULL);
    return rv != 0 ? (int64_t)rv : sid;
}

/* Negotiated ALPN token into buf; returns its length, 0 if none (or buf too small). */
size_t iq_conn_get_alpn(iq_conn *c, uint8_t *buf, size_t buflen)
{
    if (c->cptls.ptls == NULL) {
        return 0;
    }
    const char *proto = ptls_get_negotiated_protocol(c->cptls.ptls);
    if (proto == NULL) {
        return 0;
    }
    size_t len = strlen(proto);
    if (len == 0 || len > buflen) {
        return 0;
    }
    memcpy(buf, proto, len);
    return len;
}

const char *iq_strerror(int liberr)
{
    return ngtcp2_strerror(liberr);
}

/* ---- minimal client (test harness only) -------------------------------------------------- *
 * A bare ngtcp2 client with a picotls session, enough to complete a real handshake against the
 * server side above and echo one bidi stream. Not part of the public C# API - it exists so the
 * e2e suite can prove the whole engine hermetically, with no external QUIC client.             */

typedef struct iq_client_engine {
    ptls_context_t ptls_ctx;
    iq_callbacks   cbs;
    char           alpn[64];   /* the protocol every connection from this engine offers */
} iq_client_engine;

iq_client_engine *iq_client_engine_new(const char *alpn, iq_callbacks cbs)
{
    iq_client_engine *e = calloc(1, sizeof(*e));
    if (e == NULL) {
        return NULL;
    }
    e->cbs = cbs;
    if (alpn != NULL) {
        snprintf(e->alpn, sizeof(e->alpn), "%s", alpn);
    }
    e->ptls_ctx.random_bytes  = ptls_openssl_random_bytes;
    e->ptls_ctx.get_time      = &ptls_get_time;
    e->ptls_ctx.key_exchanges = ptls_openssl_key_exchanges;
    e->ptls_ctx.cipher_suites = ptls_openssl_cipher_suites;
    /* Test client: accept the server's self-signed cert unconditionally (verify_certificate NULL). */
    if (ngtcp2_crypto_picotls_configure_client_context(&e->ptls_ctx) != 0) {
        free(e);
        return NULL;
    }
    return e;
}

void iq_client_engine_free(iq_client_engine *e)
{
    free(e);
}

/* Open a client connection. scid_len is the length of the connection ID we ask the peer to send
 * back to us: it MUST match the demux slice of whoever routes our inbound datagrams (the reactor
 * uses QuicOptions.LocalCidLength), because short-header packets carry no CID length on the wire.
 * scid_out receives that CID so the caller can register the route. */
iq_conn *iq_client_connect(iq_client_engine *e,
                           const void *local_sa, size_t local_salen,
                           const void *remote_sa, size_t remote_salen,
                           const char *server_name, const char *alpn,
                           size_t scid_len, uint64_t ts, void *user,
                           uint8_t *scid_out)
{
    iq_conn *c = calloc(1, sizeof(*c));
    if (c == NULL) {
        return NULL;
    }
    c->user = user;
    c->cbs = e->cbs;
    c->on_stream_data_raw = e->cbs.on_stream_data;   // legacy raw path for the bare test drivers

    memcpy(&c->local_addr, local_sa, local_salen);
    memcpy(&c->remote_addr, remote_sa, remote_salen);
    c->path.local.addr     = &c->local_addr.sa;
    c->path.local.addrlen  = (ngtcp2_socklen)local_salen;
    c->path.remote.addr    = &c->remote_addr.sa;
    c->path.remote.addrlen = (ngtcp2_socklen)remote_salen;

    ngtcp2_callbacks callbacks = {0};
    callbacks.client_initial            = ngtcp2_crypto_client_initial_cb;
    callbacks.recv_crypto_data          = ngtcp2_crypto_recv_crypto_data_cb;
    callbacks.encrypt                   = ngtcp2_crypto_encrypt_cb;
    callbacks.decrypt                   = ngtcp2_crypto_decrypt_cb;
    callbacks.hp_mask                   = ngtcp2_crypto_hp_mask_cb;
    callbacks.recv_retry                = ngtcp2_crypto_recv_retry_cb;
    callbacks.update_key                = ngtcp2_crypto_update_key_cb;
    callbacks.delete_crypto_aead_ctx    = ngtcp2_crypto_delete_crypto_aead_ctx_cb;
    callbacks.delete_crypto_cipher_ctx  = ngtcp2_crypto_delete_crypto_cipher_ctx_cb;
    callbacks.get_path_challenge_data   = ngtcp2_crypto_get_path_challenge_data_cb;
    callbacks.version_negotiation       = ngtcp2_crypto_version_negotiation_cb;
    callbacks.rand                      = iq_rand;
    callbacks.get_new_connection_id     = iq_cb_get_new_connection_id_noreport;
    callbacks.handshake_completed       = iq_cb_handshake_completed;
    callbacks.recv_stream_data          = iq_cb_recv_stream_data;
    callbacks.stream_close              = iq_cb_stream_close;
    callbacks.stream_reset              = iq_cb_stream_reset;
    callbacks.stream_stop_sending       = iq_cb_stream_stop_sending;
    callbacks.acked_stream_data_offset  = iq_cb_acked_stream_data_offset;

    ngtcp2_settings settings;
    ngtcp2_settings_default(&settings);
    settings.initial_ts = ts;

    ngtcp2_transport_params params;
    ngtcp2_transport_params_default(&params);
    params.initial_max_stream_data_bidi_local  = 256 * 1024;
    params.initial_max_stream_data_bidi_remote = 256 * 1024;
    params.initial_max_stream_data_uni         = 256 * 1024;
    params.initial_max_data                    = 1024 * 1024;
    params.initial_max_streams_bidi            = 1024;
    params.initial_max_streams_uni             = 100;

    ngtcp2_cid dcid, scid;
    dcid.datalen = 16; ptls_openssl_random_bytes(dcid.data, dcid.datalen);
    scid.datalen = (scid_len > 0 && scid_len <= NGTCP2_MAX_CIDLEN) ? scid_len : 16;
    ptls_openssl_random_bytes(scid.data, scid.datalen);

    if (ngtcp2_conn_client_new(&c->conn, &dcid, &scid, &c->path, NGTCP2_PROTO_VER_V1,
                               &callbacks, &settings, &params, NULL, c) != 0) {
        free(c);
        return NULL;
    }

    c->conn_ref.get_conn  = iq_get_conn;
    c->conn_ref.user_data = c;

    ngtcp2_crypto_picotls_ctx_init(&c->cptls);
    c->cptls.ptls = ptls_new(&e->ptls_ctx, 0 /* client */);
    if (c->cptls.ptls == NULL) {
        ngtcp2_conn_del(c->conn);
        free(c);
        return NULL;
    }
    *ptls_get_data_ptr(c->cptls.ptls) = &c->conn_ref;
    ptls_set_server_name(c->cptls.ptls, server_name, strlen(server_name));

    c->exts[1].type = UINT16_MAX;
    c->cptls.handshake_properties.additional_extensions = c->exts;

    if (ngtcp2_crypto_picotls_configure_client_session(&c->cptls, c->conn) != 0) {
        ptls_free(c->cptls.ptls);
        ngtcp2_conn_del(c->conn);
        free(c);
        return NULL;
    }

    /* ALPN: RFC 9001 requires QUIC clients to offer one, and a server pinned to "h3" fails the
     * handshake (no_application_protocol -> ERR_CRYPTO) against a client that offers none. The
     * iovec must outlive the handshake, so it points into the connection's own buffer. */
    const char *offer = (alpn != NULL && alpn[0] != '\0') ? alpn : e->alpn;
    if (offer != NULL && offer[0] != '\0') {
        snprintf(c->alpn, sizeof(c->alpn), "%s", offer);
        c->alpn_vec.base = (uint8_t *)c->alpn;
        c->alpn_vec.len  = strlen(c->alpn);
        c->cptls.handshake_properties.client.negotiated_protocols.list  = &c->alpn_vec;
        c->cptls.handshake_properties.client.negotiated_protocols.count = 1;
    }


    ngtcp2_conn_set_tls_native_handle(c->conn, &c->cptls);
    ngtcp2_ccerr_default(&c->last_error);

    if (scid_out != NULL) {
        memcpy(scid_out, scid.data, scid.datalen);
    }
    return c;
}

/* Open a client bidi stream; returns the stream id or a negative error. */
int64_t iq_client_open_bidi(iq_conn *c)
{
    int64_t sid;
    if (ngtcp2_conn_open_bidi_stream(c->conn, &sid, NULL) != 0) {
        return -1;
    }
    return sid;
}

/* ---- app-paced receive windows ----------------------------------------------------------- */

/* Mark/unmark a stream as app-paced (see iq_conn.paced). Reactor thread only. */
void iq_conn_set_stream_paced(iq_conn *c, int64_t stream_id, int on)
{
    int pi = iq_paced_index(c, stream_id);
    if (on) {
        if (pi < 0 && c->paced_count < (int)(sizeof(c->paced) / sizeof(c->paced[0]))) {
            c->paced[c->paced_count++] = stream_id;
        }
    } else if (pi >= 0) {
        c->paced[pi] = c->paced[--c->paced_count];
    }
}

/* Open the peer's flow-control window (stream + connection) for bytes the app consumed on a
 * paced stream. Extending is permission, never obligation - over-crediting is harmless. */
void iq_conn_consume(iq_conn *c, int64_t stream_id, uint64_t n)
{
    ngtcp2_conn_extend_max_offset(c->conn, n);
    ngtcp2_conn_extend_max_stream_offset(c->conn, stream_id, n);
}
