# dropped

Code that used to ship and no longer does. It is kept because it was real, it was measured, and the
reasoning behind retiring it is easier to follow with the thing itself still readable.

Nothing here is in `ioxide.slnx`, nothing here is built by CI, and nothing here is published to
NuGet. It will not compile against the current tree forever, and that is expected - if you need it,
take it from the last release tag that shipped it rather than from here.

## ioxide.nghttp2

The nghttp2 binding: HTTP/2 framing, HPACK and flow control from the reference C implementation,
driven sans-I/O over an `IDuplexPipe`.

It was replaced by `ioxide.http2`, which does the same job in pure C#. That started as the
drop-in-without-a-native-library option and ended as the only one:

- **It measured at least as well.** Interleaved warm runs put the two within `0.98x`-`1.09x` of each
  other on a small body; where they diverged, the ordering depended on the connection-to-reactor
  ratio rather than on the codec.
- **It grew past what the binding could reach.** Streamed responses, streamed request bodies and
  non-blocking dispatch all landed on the managed side. The binding kept the blocking dispatch loop
  it was written with, where one slow handler held up every other stream on the connection.
- **Two implementations of one protocol is a tax on every change**, paid in samples, docs, tests and
  benchmark fixtures, and the second one was no longer buying coverage of the protocol's darker
  corners - it was buying a native build step.

The last thing holding it in the tree was `ioxide.httpclient`, whose HTTP/2 *client* was built on
it. That client is pure C# now too, on the same framing and HPACK the server uses, so the binding
had no callers left.

`build-nghttp2-native.sh` built the native library it bound to. `Playground.Http2.Nghttp2` was its
h2c sample; `Playground/Http2/Managed` is the same server on the managed stack.
