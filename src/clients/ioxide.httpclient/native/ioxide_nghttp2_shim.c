/*
 * ioxide.nghttp2 shim: a flat C facade over nghttp2 for the managed HTTP/2 CLIENT.
 *
 * nghttp2 is sans-I/O, like nghttp3: bytes in via nghttp2_session_mem_recv, bytes out via
 * nghttp2_session_mem_send. No sockets, no TLS, no threads - the transport is whatever
 * TcpConnection the C# side rides on. That is the whole reason this is a good fit for the reactor:
 * ioxide owns the I/O and the loop, nghttp2 owns only the protocol state machine.
 *
 *   conn = ih2_client_new(cbs, user)          one per TCP connection (client preface queued)
 *          ih2_submit_request(hdrs, body)     headers packed [u16 nlen][name][u16 vlen][value]*
 *          ih2_read(data, len)                feed received bytes; response events fire back
 *          ih2_write(buf, len)                drain one egress chunk per call until 0
 *          ih2_free
 *
 * Every call happens on the owning reactor thread. Request bodies are copied in and freed on
 * stream close, so the C# side keeps nothing alive across the call. Callbacks only DEPOSIT state -
 * the managed side must not re-enter nghttp2 from inside one, so the bridge records completions
 * and resumes its waiters after mem_recv returns.
 */
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include <nghttp2/nghttp2.h>

/* ---- callback table into C# ------------------------------------------------------------- */

typedef struct ih2_callbacks {
    void (*on_begin_headers)(void *user, int32_t stream_id);
    void (*on_header)(void *user, int32_t stream_id,
                      const uint8_t *name, size_t namelen,
                      const uint8_t *value, size_t valuelen);
    void (*on_end_headers)(void *user, int32_t stream_id);
    void (*on_data)(void *user, int32_t stream_id, const uint8_t *data, size_t datalen);
    void (*on_end_stream)(void *user, int32_t stream_id);
    /* Stream died without completing (RST_STREAM, GOAWAY, connection error). */
    void (*on_stream_error)(void *user, int32_t stream_id, uint32_t error_code);
} ih2_callbacks;

/* Per-stream request body, owned here until the stream closes. */
typedef struct ih2_stream {
    struct ih2_stream *next;
    int32_t  stream_id;
    uint8_t *body;
    size_t   body_len;
    size_t   body_sent;
} ih2_stream;

typedef struct ih2_conn {
    nghttp2_session *session;
    ih2_callbacks    cbs;
    void            *user;
    ih2_stream      *streams;
} ih2_conn;

static ih2_stream *ih2_stream_find(ih2_conn *c, int32_t stream_id)
{
    for (ih2_stream *s = c->streams; s != NULL; s = s->next) {
        if (s->stream_id == stream_id) {
            return s;
        }
    }
    return NULL;
}

static void ih2_stream_drop(ih2_conn *c, int32_t stream_id)
{
    ih2_stream **link = &c->streams;
    while (*link != NULL) {
        if ((*link)->stream_id == stream_id) {
            ih2_stream *dead = *link;
            *link = dead->next;
            free(dead->body);
            free(dead);
            return;
        }
        link = &(*link)->next;
    }
}

/* ---- nghttp2 callbacks ------------------------------------------------------------------- */

static int on_begin_headers(nghttp2_session *session, const nghttp2_frame *frame, void *user_data)
{
    (void)session;
    ih2_conn *c = user_data;
    /* HCAT_HEADERS too, not just HCAT_RESPONSE: nghttp2 categorises only the FIRST section as
     * the response, so after a 1xx (103 Early Hints, 100 Continue) the real response arrives as
     * HCAT_HEADERS. Reporting only the first section left the managed side treating the interim
     * one as final and slicing the body from the wrong offset. */
    if (frame->hd.type == NGHTTP2_HEADERS &&
        (frame->headers.cat == NGHTTP2_HCAT_RESPONSE || frame->headers.cat == NGHTTP2_HCAT_HEADERS) &&
        c->cbs.on_begin_headers != NULL) {
        c->cbs.on_begin_headers(c->user, frame->hd.stream_id);
    }
    return 0;
}

static int on_header(nghttp2_session *session, const nghttp2_frame *frame,
                     const uint8_t *name, size_t namelen,
                     const uint8_t *value, size_t valuelen, uint8_t flags, void *user_data)
{
    (void)session; (void)flags;
    ih2_conn *c = user_data;
    if (frame->hd.type == NGHTTP2_HEADERS && c->cbs.on_header != NULL) {
        c->cbs.on_header(c->user, frame->hd.stream_id, name, namelen, value, valuelen);
    }
    return 0;
}

static int on_frame_recv(nghttp2_session *session, const nghttp2_frame *frame, void *user_data)
{
    (void)session;
    ih2_conn *c = user_data;

    if (frame->hd.type == NGHTTP2_HEADERS &&
        (frame->headers.cat == NGHTTP2_HCAT_RESPONSE || frame->headers.cat == NGHTTP2_HCAT_HEADERS) &&
        c->cbs.on_end_headers != NULL) {
        c->cbs.on_end_headers(c->user, frame->hd.stream_id);
    }

    /* END_STREAM can ride a HEADERS frame (bodyless response) or a DATA frame. */
    if ((frame->hd.flags & NGHTTP2_FLAG_END_STREAM) != 0 &&
        (frame->hd.type == NGHTTP2_HEADERS || frame->hd.type == NGHTTP2_DATA) &&
        c->cbs.on_end_stream != NULL) {
        c->cbs.on_end_stream(c->user, frame->hd.stream_id);
    }

    return 0;
}

static int on_data_chunk_recv(nghttp2_session *session, uint8_t flags, int32_t stream_id,
                              const uint8_t *data, size_t len, void *user_data)
{
    (void)session; (void)flags;
    ih2_conn *c = user_data;
    if (c->cbs.on_data != NULL) {
        c->cbs.on_data(c->user, stream_id, data, len);
    }
    return 0;
}

static int on_stream_close(nghttp2_session *session, int32_t stream_id, uint32_t error_code,
                           void *user_data)
{
    (void)session;
    ih2_conn *c = user_data;

    /* NO_ERROR here is the normal end of a stream whose response already completed, so only a
     * real error is worth waking the managed side for. */
    if (error_code != NGHTTP2_NO_ERROR && c->cbs.on_stream_error != NULL) {
        c->cbs.on_stream_error(c->user, stream_id, error_code);
    }

    ih2_stream_drop(c, stream_id);
    return 0;
}

/* Copy the next slice of the request body into nghttp2's buffer.
 *
 * Deliberately NOT NGHTTP2_DATA_FLAG_NO_COPY. With NO_COPY nghttp2 hands the frame to
 * send_data_callback and, on its return, accounts the frame as sent and debits the flow-control
 * window WITHOUT ever writing it to the mem_send buffer - so unless that callback does the framing
 * itself, every DATA frame vanishes and the request hangs at the peer. The body is already copied
 * into s->body at submit time, so a plain copy here costs nothing extra and keeps the framing
 * inside nghttp2 where it belongs. */
static ssize_t read_body(nghttp2_session *session, int32_t stream_id, uint8_t *buf, size_t length,
                         uint32_t *data_flags, nghttp2_data_source *source, void *user_data)
{
    (void)session; (void)source;
    ih2_conn *c = user_data;
    ih2_stream *s = ih2_stream_find(c, stream_id);

    if (s == NULL || s->body_len == 0) {
        *data_flags |= NGHTTP2_DATA_FLAG_EOF;
        return 0;
    }

    size_t remaining = s->body_len - s->body_sent;
    size_t take = remaining < length ? remaining : length;
    memcpy(buf, s->body + s->body_sent, take);
    s->body_sent += take;

    if (s->body_sent == s->body_len) {
        *data_flags |= NGHTTP2_DATA_FLAG_EOF;
    }
    return (ssize_t)take;
}

/* ---- API --------------------------------------------------------------------------------- */

ih2_conn *ih2_client_new(ih2_callbacks cbs, void *user)
{
    ih2_conn *c = calloc(1, sizeof(*c));
    if (c == NULL) {
        return NULL;
    }
    c->cbs  = cbs;
    c->user = user;

    nghttp2_session_callbacks *callbacks;
    if (nghttp2_session_callbacks_new(&callbacks) != 0) {
        free(c);
        return NULL;
    }
    nghttp2_session_callbacks_set_on_begin_headers_callback(callbacks, on_begin_headers);
    nghttp2_session_callbacks_set_on_header_callback(callbacks, on_header);
    nghttp2_session_callbacks_set_on_frame_recv_callback(callbacks, on_frame_recv);
    nghttp2_session_callbacks_set_on_data_chunk_recv_callback(callbacks, on_data_chunk_recv);
    nghttp2_session_callbacks_set_on_stream_close_callback(callbacks, on_stream_close);

    int rv = nghttp2_session_client_new(&c->session, callbacks, c);
    nghttp2_session_callbacks_del(callbacks);
    if (rv != 0) {
        free(c);
        return NULL;
    }

    /* Client connection preface + our SETTINGS; the first ih2_write drain carries them out. */
    nghttp2_settings_entry settings[] = {
        {NGHTTP2_SETTINGS_ENABLE_PUSH, 0},   /* nothing routes a pushed stream; do not accept any */
        {NGHTTP2_SETTINGS_MAX_CONCURRENT_STREAMS, 1000},
        {NGHTTP2_SETTINGS_INITIAL_WINDOW_SIZE, 1 << 20},
    };
    if (nghttp2_submit_settings(c->session, NGHTTP2_FLAG_NONE, settings, 3) != 0) {
        nghttp2_session_del(c->session);
        free(c);
        return NULL;
    }

    return c;
}

/* Headers arrive packed as [u16 namelen][name][u16 valuelen][value]... - the same format the
 * nghttp3 shim takes, so the managed packing code is shared in shape. Returns the stream id, or a
 * negative nghttp2 error. */
int32_t ih2_submit_request(ih2_conn *c, const uint8_t *headers, size_t headers_len,
                           const uint8_t *body, size_t body_len)
{
    if (c == NULL || c->session == NULL) {
        return NGHTTP2_ERR_INVALID_STATE;
    }

    enum { MAX_HEADERS = 64 };
    nghttp2_nv nv[MAX_HEADERS];
    size_t count = 0;

    size_t off = 0;
    while (off + 2 <= headers_len && count < MAX_HEADERS) {
        uint16_t namelen;
        memcpy(&namelen, headers + off, 2);
        off += 2;
        if (off + namelen + 2 > headers_len) {
            return NGHTTP2_ERR_INVALID_ARGUMENT;
        }
        const uint8_t *name = headers + off;
        off += namelen;

        uint16_t valuelen;
        memcpy(&valuelen, headers + off, 2);
        off += 2;
        if (off + valuelen > headers_len) {
            return NGHTTP2_ERR_INVALID_ARGUMENT;
        }
        const uint8_t *value = headers + off;
        off += valuelen;

        nv[count].name     = (uint8_t *)name;
        nv[count].namelen  = namelen;
        nv[count].value    = (uint8_t *)value;
        nv[count].valuelen = valuelen;
        nv[count].flags    = NGHTTP2_NV_FLAG_NONE;   /* nghttp2 copies during submit */
        count++;
    }

    /* Truncating here would silently drop headers - an omitted authorization is worse than a
     * failed request, so refuse instead. */
    if (off < headers_len) {
        return NGHTTP2_ERR_INVALID_ARGUMENT;
    }

    ih2_stream *s = NULL;
    nghttp2_data_provider provider;
    nghttp2_data_provider *provider_ptr = NULL;

    if (body_len > 0) {
        s = calloc(1, sizeof(*s));
        if (s == NULL) {
            return NGHTTP2_ERR_NOMEM;
        }
        s->body = malloc(body_len);
        if (s->body == NULL) {
            free(s);
            return NGHTTP2_ERR_NOMEM;
        }
        memcpy(s->body, body, body_len);
        s->body_len = body_len;

        provider.source.ptr = s;
        provider.read_callback = read_body;
        provider_ptr = &provider;
    }

    int32_t stream_id = nghttp2_submit_request(c->session, NULL, nv, count, provider_ptr, NULL);
    if (stream_id < 0) {
        if (s != NULL) {
            free(s->body);
            free(s);
        }
        return stream_id;
    }

    if (s != NULL) {
        s->stream_id = stream_id;
        s->next = c->streams;
        c->streams = s;
    }

    return stream_id;
}

/* Feed received bytes. Returns bytes consumed, or a negative nghttp2 error. */
ssize_t ih2_read(ih2_conn *c, const uint8_t *data, size_t datalen)
{
    if (c == NULL || c->session == NULL) {
        return NGHTTP2_ERR_INVALID_STATE;
    }
    return nghttp2_session_mem_recv(c->session, data, datalen);
}

/* Drain one egress chunk into buf. Returns bytes written (0 = nothing left), or negative on
 * error. nghttp2 owns the returned pointer, so the bytes are copied out before returning. */
ssize_t ih2_write(ih2_conn *c, uint8_t *buf, size_t buflen)
{
    if (c == NULL || c->session == NULL) {
        return NGHTTP2_ERR_INVALID_STATE;
    }

    /* Largest chunk mem_send can hand back: a 16 KiB frame payload plus its 9-byte header.
     * nghttp2 caps frames at NGHTTP2_MAX_FRAME_SIZE_MIN regardless of the peer's SETTINGS. */
    enum { MAX_CHUNK = 16384 + 9 };

    size_t total = 0;

    while (buflen - total >= MAX_CHUNK) {
        /* Checked BEFORE pulling, and that ordering is the whole point: mem_send runs
         * session_after_frame_sent1 before it returns, so a chunk it hands back is already
         * accounted as sent and is never offered again. Discarding one because it did not fit
         * would lose the frame permanently. Stopping early just leaves it queued for the next
         * call. */
        const uint8_t *chunk;
        ssize_t produced = nghttp2_session_mem_send(c->session, &chunk);
        if (produced < 0) {
            return produced;
        }
        if (produced == 0) {
            break;
        }
        memcpy(buf + total, chunk, (size_t)produced);
        total += (size_t)produced;
    }

    return (ssize_t)total;
}

/* Non-zero once the session can neither send nor receive - the bridge drops the connection. */
int ih2_is_dead(ih2_conn *c)
{
    if (c == NULL || c->session == NULL) {
        return 1;
    }
    return nghttp2_session_want_read(c->session) == 0 &&
           nghttp2_session_want_write(c->session) == 0;
}

void ih2_free(ih2_conn *c)
{
    if (c == NULL) {
        return;
    }
    while (c->streams != NULL) {
        ih2_stream_drop(c, c->streams->stream_id);
    }
    nghttp2_session_del(c->session);
    free(c);
}

const char *ih2_strerror(int error_code)
{
    return nghttp2_strerror(error_code);
}
