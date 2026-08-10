using System.Buffers.Binary;
using System.IO.Pipelines;
using ioxide.http2;

namespace Ioxide.Tests;

/// <summary>
/// The HTTP/2 write queue: staging and flushing take turns on the connection's pipe, so a handler
/// that completes while a transport flush is in flight queues its frames and they leave - together
/// with every other completion from that window - on the single flush after it.
///
/// The fake transport here enforces the same contract as the real TcpConnection: any write while a
/// flush is in flight throws, and so does a second flush. That is the constraint the queue exists
/// to solve, so a test that passed against a lenient fake would prove nothing.
/// </summary>
internal static class Http2OutputQueueTests
{
    public static void Register(Runner runner)
    {
        runner.Test("h2 queue: a pass's responses still leave on one flush", () =>
        {
            using var client = new StrictClient();
            Task run = client.Connection.RunBufferedAsync(_ => Http2Response.Text("hi"));

            client.ReleaseFlush();                       // server SETTINGS
            client.SendRequests(1, 3);

            // One pass: SETTINGS ack plus both responses, staged together, flushed once.
            Assert.Equal(1, client.PendingFlushes);
            byte[] pass = client.ReleaseFlush();

            List<Frame> frames = Frame.Walk(pass);
            Assert.True(frames.Any(f => f.Type == 0x4 && (f.Flags & 0x1) != 0), "settings ack in pass flush");
            AssertResponse(frames, streamId: 1);
            AssertResponse(frames, streamId: 3);

            client.Close(run);
        });

        runner.Test("h2 queue: completions during a flush share the next one", () =>
        {
            using var client = new StrictClient();

            var parked = new Dictionary<int, TaskCompletionSource<Http2Response>>();
            Task run = client.Connection.RunBufferedAsync(request =>
            {
                var tcs = new TaskCompletionSource<Http2Response>();
                parked[request.StreamId] = tcs;
                return new ValueTask<Http2Response>(tcs.Task);
            });

            client.ReleaseFlush();                       // server SETTINGS
            client.SendRequests(1, 3);

            // The pass flushed only the SETTINGS ack; both handlers are parked. Complete them
            // while that flush is still in flight - exactly where the old code threw
            // "Cannot write while flush is in progress" and killed the connection.
            Assert.Equal(1, client.PendingFlushes);
            parked[1].SetResult(Http2Response.Text("first"));
            parked[3].SetResult(Http2Response.Text("second"));

            Assert.Equal(1, client.PendingFlushes);      // both queued; nothing new in flight
            client.ReleaseFlush();                       // the ack

            // ONE flush now carries both responses - the write the queue exists to coalesce.
            Assert.Equal(1, client.PendingFlushes);
            List<Frame> frames = Frame.Walk(client.ReleaseFlush());
            AssertResponse(frames, streamId: 1);
            AssertResponse(frames, streamId: 3);

            client.Close(run);
        });

        runner.Test("h2 queue: a streamed body resumed outside the pass keeps frame order", () =>
        {
            using var client = new StrictClient();

            var gate = new TaskCompletionSource();
            Task run = client.Connection.RunAsync(async (_, writer) =>
            {
                writer.WriteHeaders(new Http2Response { Status = 200 });
                Push(writer, "live");
                await writer.FlushAsync();               // inside the pass: rides the pass flush
                await gate.Task;                         // park past the pass, like a slow upstream
                Push(writer, "wire");
                await writer.FlushAsync();               // outside: a real transport write
            });

            client.ReleaseFlush();                       // server SETTINGS
            client.SendRequests(1);

            List<Frame> pass = Frame.Walk(client.ReleaseFlush());
            Assert.True(pass.Any(f => f.Type == 0x1 && f.StreamId == 1), "HEADERS rode the pass flush");
            Assert.True(pass.Any(f => f is { Type: 0x0, StreamId: 1, Length: 4, EndStream: false }),
                "first chunk rode the pass flush, stream open");

            gate.SetResult();                            // resume: chunk, then END_STREAM on complete
            List<Frame> chunk = Frame.Walk(client.ReleaseFlush());
            Assert.True(chunk.Any(f => f is { Type: 0x0, StreamId: 1, Length: 4, EndStream: false }),
                "second chunk flushed on resume");

            List<Frame> fin = Frame.Walk(client.ReleaseFlush());
            Assert.True(fin.Any(f => f is { Type: 0x0, StreamId: 1, Length: 0, EndStream: true }),
                "completion sent END_STREAM");

            client.Close(run);
        });

        runner.Test("h2 queue: a faulted flush wakes parked writers instead of stranding them", () =>
        {
            using var client = new StrictClient();

            var parked = new Dictionary<int, TaskCompletionSource<Http2Response>>();
            Task run = client.Connection.RunBufferedAsync(request =>
            {
                var tcs = new TaskCompletionSource<Http2Response>();
                parked[request.StreamId] = tcs;
                return new ValueTask<Http2Response>(tcs.Task);
            });

            client.ReleaseFlush();                       // server SETTINGS
            client.SendRequests(1);

            // The completion queues behind the in-flight ack flush and parks on its turn. Then the
            // transport dies. The waiter must wake and the connection must finish, not hang.
            parked[1].SetResult(Http2Response.Text("never sent"));
            client.FaultFlush();

            Assert.True(run.Wait(5_000), "connection wound down after the transport fault");
        });
    }

    private static void Push(Http2ResponseWriter writer, string text)
    {
        Span<byte> span = writer.GetSpan(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            span[i] = (byte)text[i];
        }
        writer.Advance(text.Length);
    }

    private static void AssertResponse(List<Frame> frames, int streamId)
    {
        Assert.True(frames.Any(f => f.Type == 0x1 && f.StreamId == streamId),
            $"HEADERS for stream {streamId}");
        Assert.True(frames.Any(f => f is { Type: 0x0, EndStream: true } && f.StreamId == streamId),
            $"DATA with END_STREAM for stream {streamId}");
    }

    private readonly record struct Frame(int Length, byte Type, byte Flags, int StreamId)
    {
        public bool EndStream => (Flags & 0x1) != 0;

        public static List<Frame> Walk(ReadOnlySpan<byte> wire)
        {
            var frames = new List<Frame>();
            int at = 0;
            while (at + 9 <= wire.Length)
            {
                int length = (wire[at] << 16) | (wire[at + 1] << 8) | wire[at + 2];
                byte type = wire[at + 3];
                byte flags = wire[at + 4];
                int stream = (int)(BinaryPrimitives.ReadUInt32BigEndian(wire[(at + 5)..]) & 0x7FFFFFFF);
                frames.Add(new Frame(length, type, flags, stream));
                at += 9 + length;
            }
            Assert.Equal(wire.Length, at);   // no torn frame: every byte accounted for
            return frames;
        }
    }

    /// <summary>
    /// An HTTP/2 client the test steps by hand: an inline input pipe to feed frames, and a strict
    /// output writer whose flushes complete only when the test releases them.
    /// </summary>
    private sealed class StrictClient : IDuplexPipe, IDisposable
    {
        private readonly Pipe _input = new(new PipeOptions(
            readerScheduler: PipeScheduler.Inline,
            writerScheduler: PipeScheduler.Inline,
            useSynchronizationContext: false));

        private readonly StrictWriter _writer = new();

        public StrictClient() => Connection = new Http2Connection(this);

        public Http2Connection Connection { get; }

        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _writer;

        public int PendingFlushes => _writer.PendingFlushes;
        public byte[] ReleaseFlush() => _writer.ReleaseFlush();
        public void FaultFlush() => _writer.FaultFlush();

        /// <summary>Preface, an empty SETTINGS, then one indexed-HPACK GET per stream id.</summary>
        public void SendRequests(params int[] streamIds)
        {
            var bytes = new List<byte>("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray());
            bytes.AddRange(FrameHeaderBytes(0, 0x4, 0, 0));
            foreach (int streamId in streamIds)
            {
                // 0x82 :method GET, 0x86 :scheme http, 0x84 :path / - static table only, so the
                // request needs no HPACK encoder of its own.
                bytes.AddRange(FrameHeaderBytes(3, 0x1, 0x5, streamId));
                bytes.AddRange([0x82, 0x86, 0x84]);
            }
            Feed(bytes.ToArray());
        }

        public void Feed(byte[] bytes)
            => _input.Writer.WriteAsync(bytes).GetAwaiter().GetResult();

        /// <summary>End the input and wait out the connection's run task.</summary>
        public void Close(Task run)
        {
            _input.Writer.Complete();
            Assert.True(run.Wait(5_000), "connection wound down");
        }

        public void Dispose() => Connection.Dispose();

        private static byte[] FrameHeaderBytes(int length, byte type, byte flags, int streamId) =>
        [
            (byte)(length >> 16), (byte)(length >> 8), (byte)length,
            type, flags,
            (byte)(streamId >> 24), (byte)(streamId >> 16), (byte)(streamId >> 8), (byte)streamId,
        ];
    }

    /// <summary>
    /// The real transport's write contract, distilled: writes throw while a flush is in flight, a
    /// second flush throws, and a flush completes only when the reactor - here, the test - says so.
    /// </summary>
    private sealed class StrictWriter : PipeWriter
    {
        private readonly List<byte> _staged = [];
        private readonly Queue<(TaskCompletionSource<FlushResult> Signal, byte[] Payload)> _inFlight = new();
        private byte[] _scratch = new byte[4096];
        private bool _flushing;

        public int PendingFlushes => _inFlight.Count;

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            ThrowIfFlushing();
            EnsureScratch(sizeHint);
            return _scratch;
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            ThrowIfFlushing();
            EnsureScratch(sizeHint);
            return _scratch;
        }

        public override void Advance(int bytes)
        {
            ThrowIfFlushing();
            _staged.AddRange(_scratch.AsSpan(0, bytes));
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (_flushing)
            {
                throw new InvalidOperationException("FlushAsync already in progress.");
            }
            if (_staged.Count == 0)
            {
                return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
            }

            _flushing = true;
            var signal = new TaskCompletionSource<FlushResult>();
            _inFlight.Enqueue((signal, _staged.ToArray()));
            _staged.Clear();
            return new ValueTask<FlushResult>(signal.Task);
        }

        /// <summary>Complete the oldest in-flight flush and hand back the bytes it carried.</summary>
        public byte[] ReleaseFlush()
        {
            Assert.True(_inFlight.Count > 0, "a flush was expected to be in flight");
            (TaskCompletionSource<FlushResult> signal, byte[] payload) = _inFlight.Dequeue();

            // Writable again BEFORE the signal: the queue drain runs inline on SetResult and its
            // first act is to write.
            _flushing = false;
            signal.SetResult(new FlushResult(isCanceled: false, isCompleted: false));
            return payload;
        }

        /// <summary>Fail the oldest in-flight flush, as a torn-down transport would.</summary>
        public void FaultFlush()
        {
            Assert.True(_inFlight.Count > 0, "a flush was expected to be in flight");
            (TaskCompletionSource<FlushResult> signal, _) = _inFlight.Dequeue();
            _flushing = false;
            signal.SetException(new IOException("transport torn down"));
        }

        public override void Complete(Exception? exception = null)
        {
        }

        public override void CancelPendingFlush()
        {
        }

        private void ThrowIfFlushing()
        {
            if (_flushing)
            {
                throw new InvalidOperationException("Cannot write while flush is in progress.");
            }
        }

        private void EnsureScratch(int sizeHint)
        {
            if (_scratch.Length < sizeHint)
            {
                _scratch = new byte[sizeHint];
            }
        }
    }
}
