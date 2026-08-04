namespace ioxide.http3;

/// <summary>The QPACK static table, RFC 9204 Appendix A - 99 fixed (name, value) entries. With the
/// dynamic table disabled (our SETTINGS advertise capacity 0) this is the only table peers may
/// reference.</summary>
internal static class QpackStatic
{
    public static readonly (byte[] Name, byte[] Value)[] Table =
    {
        (":authority"u8.ToArray(), ""u8.ToArray()),                                    // 0
        (":path"u8.ToArray(), "/"u8.ToArray()),                                        // 1
        ("age"u8.ToArray(), "0"u8.ToArray()),                                          // 2
        ("content-disposition"u8.ToArray(), ""u8.ToArray()),                           // 3
        ("content-length"u8.ToArray(), "0"u8.ToArray()),                               // 4
        ("cookie"u8.ToArray(), ""u8.ToArray()),                                        // 5
        ("date"u8.ToArray(), ""u8.ToArray()),                                          // 6
        ("etag"u8.ToArray(), ""u8.ToArray()),                                          // 7
        ("if-modified-since"u8.ToArray(), ""u8.ToArray()),                             // 8
        ("if-none-match"u8.ToArray(), ""u8.ToArray()),                                 // 9
        ("last-modified"u8.ToArray(), ""u8.ToArray()),                                 // 10
        ("link"u8.ToArray(), ""u8.ToArray()),                                          // 11
        ("location"u8.ToArray(), ""u8.ToArray()),                                      // 12
        ("referer"u8.ToArray(), ""u8.ToArray()),                                       // 13
        ("set-cookie"u8.ToArray(), ""u8.ToArray()),                                    // 14
        (":method"u8.ToArray(), "CONNECT"u8.ToArray()),                                // 15
        (":method"u8.ToArray(), "DELETE"u8.ToArray()),                                 // 16
        (":method"u8.ToArray(), "GET"u8.ToArray()),                                    // 17
        (":method"u8.ToArray(), "HEAD"u8.ToArray()),                                   // 18
        (":method"u8.ToArray(), "OPTIONS"u8.ToArray()),                                // 19
        (":method"u8.ToArray(), "POST"u8.ToArray()),                                   // 20
        (":method"u8.ToArray(), "PUT"u8.ToArray()),                                    // 21
        (":scheme"u8.ToArray(), "http"u8.ToArray()),                                   // 22
        (":scheme"u8.ToArray(), "https"u8.ToArray()),                                  // 23
        (":status"u8.ToArray(), "103"u8.ToArray()),                                    // 24
        (":status"u8.ToArray(), "200"u8.ToArray()),                                    // 25
        (":status"u8.ToArray(), "304"u8.ToArray()),                                    // 26
        (":status"u8.ToArray(), "404"u8.ToArray()),                                    // 27
        (":status"u8.ToArray(), "503"u8.ToArray()),                                    // 28
        ("accept"u8.ToArray(), "*/*"u8.ToArray()),                                     // 29
        ("accept"u8.ToArray(), "application/dns-message"u8.ToArray()),                 // 30
        ("accept-encoding"u8.ToArray(), "gzip, deflate, br"u8.ToArray()),              // 31
        ("accept-ranges"u8.ToArray(), "bytes"u8.ToArray()),                            // 32
        ("access-control-allow-headers"u8.ToArray(), "cache-control"u8.ToArray()),     // 33
        ("access-control-allow-headers"u8.ToArray(), "content-type"u8.ToArray()),      // 34
        ("access-control-allow-origin"u8.ToArray(), "*"u8.ToArray()),                  // 35
        ("cache-control"u8.ToArray(), "max-age=0"u8.ToArray()),                        // 36
        ("cache-control"u8.ToArray(), "max-age=2592000"u8.ToArray()),                  // 37
        ("cache-control"u8.ToArray(), "max-age=604800"u8.ToArray()),                   // 38
        ("cache-control"u8.ToArray(), "no-cache"u8.ToArray()),                         // 39
        ("cache-control"u8.ToArray(), "no-store"u8.ToArray()),                         // 40
        ("cache-control"u8.ToArray(), "public, max-age=31536000"u8.ToArray()),         // 41
        ("content-encoding"u8.ToArray(), "br"u8.ToArray()),                            // 42
        ("content-encoding"u8.ToArray(), "gzip"u8.ToArray()),                          // 43
        ("content-type"u8.ToArray(), "application/dns-message"u8.ToArray()),           // 44
        ("content-type"u8.ToArray(), "application/javascript"u8.ToArray()),            // 45
        ("content-type"u8.ToArray(), "application/json"u8.ToArray()),                  // 46
        ("content-type"u8.ToArray(), "application/x-www-form-urlencoded"u8.ToArray()), // 47
        ("content-type"u8.ToArray(), "image/gif"u8.ToArray()),                         // 48
        ("content-type"u8.ToArray(), "image/jpeg"u8.ToArray()),                        // 49
        ("content-type"u8.ToArray(), "image/png"u8.ToArray()),                         // 50
        ("content-type"u8.ToArray(), "text/css"u8.ToArray()),                          // 51
        ("content-type"u8.ToArray(), "text/html; charset=utf-8"u8.ToArray()),          // 52
        ("content-type"u8.ToArray(), "text/plain"u8.ToArray()),                        // 53
        ("content-type"u8.ToArray(), "text/plain;charset=utf-8"u8.ToArray()),          // 54
        ("range"u8.ToArray(), "bytes=0-"u8.ToArray()),                                 // 55
        ("strict-transport-security"u8.ToArray(), "max-age=31536000"u8.ToArray()),     // 56
        ("strict-transport-security"u8.ToArray(), "max-age=31536000; includesubdomains"u8.ToArray()),          // 57
        ("strict-transport-security"u8.ToArray(), "max-age=31536000; includesubdomains; preload"u8.ToArray()), // 58
        ("vary"u8.ToArray(), "accept-encoding"u8.ToArray()),                           // 59
        ("vary"u8.ToArray(), "origin"u8.ToArray()),                                    // 60
        ("x-content-type-options"u8.ToArray(), "nosniff"u8.ToArray()),                 // 61
        ("x-xss-protection"u8.ToArray(), "1; mode=block"u8.ToArray()),                 // 62
        (":status"u8.ToArray(), "100"u8.ToArray()),                                    // 63
        (":status"u8.ToArray(), "204"u8.ToArray()),                                    // 64
        (":status"u8.ToArray(), "206"u8.ToArray()),                                    // 65
        (":status"u8.ToArray(), "302"u8.ToArray()),                                    // 66
        (":status"u8.ToArray(), "400"u8.ToArray()),                                    // 67
        (":status"u8.ToArray(), "403"u8.ToArray()),                                    // 68
        (":status"u8.ToArray(), "421"u8.ToArray()),                                    // 69
        (":status"u8.ToArray(), "425"u8.ToArray()),                                    // 70
        (":status"u8.ToArray(), "500"u8.ToArray()),                                    // 71
        ("accept-language"u8.ToArray(), ""u8.ToArray()),                               // 72
        ("access-control-allow-credentials"u8.ToArray(), "FALSE"u8.ToArray()),         // 73
        ("access-control-allow-credentials"u8.ToArray(), "TRUE"u8.ToArray()),          // 74
        ("access-control-allow-headers"u8.ToArray(), "*"u8.ToArray()),                 // 75
        ("access-control-allow-methods"u8.ToArray(), "get"u8.ToArray()),               // 76
        ("access-control-allow-methods"u8.ToArray(), "get, post, options"u8.ToArray()),// 77
        ("access-control-allow-methods"u8.ToArray(), "options"u8.ToArray()),           // 78
        ("access-control-expose-headers"u8.ToArray(), "content-length"u8.ToArray()),   // 79
        ("access-control-request-headers"u8.ToArray(), "content-type"u8.ToArray()),    // 80
        ("access-control-request-method"u8.ToArray(), "get"u8.ToArray()),              // 81
        ("access-control-request-method"u8.ToArray(), "post"u8.ToArray()),             // 82
        ("alt-svc"u8.ToArray(), "clear"u8.ToArray()),                                  // 83
        ("authorization"u8.ToArray(), ""u8.ToArray()),                                 // 84
        ("content-security-policy"u8.ToArray(), "script-src 'none'; object-src 'none'; base-uri 'none'"u8.ToArray()), // 85
        ("early-data"u8.ToArray(), "1"u8.ToArray()),                                   // 86
        ("expect-ct"u8.ToArray(), ""u8.ToArray()),                                     // 87
        ("forwarded"u8.ToArray(), ""u8.ToArray()),                                     // 88
        ("if-range"u8.ToArray(), ""u8.ToArray()),                                      // 89
        ("origin"u8.ToArray(), ""u8.ToArray()),                                        // 90
        ("purpose"u8.ToArray(), "prefetch"u8.ToArray()),                               // 91
        ("server"u8.ToArray(), ""u8.ToArray()),                                        // 92
        ("timing-allow-origin"u8.ToArray(), "*"u8.ToArray()),                          // 93
        ("upgrade-insecure-requests"u8.ToArray(), "1"u8.ToArray()),                    // 94
        ("user-agent"u8.ToArray(), ""u8.ToArray()),                                    // 95
        ("x-forwarded-for"u8.ToArray(), ""u8.ToArray()),                               // 96
        ("x-frame-options"u8.ToArray(), "deny"u8.ToArray()),                           // 97
        ("x-frame-options"u8.ToArray(), "sameorigin"u8.ToArray()),                     // 98
    };
}
