using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using ioxide;
using ioxide.tls;

namespace Ioxide.Tests;

/// <summary>
/// Serving TLS over pipes. The write half needs nothing - kTLS TX means plaintext goes out through
/// the ordinary pipe - so everything here is about the read half, where plaintext has to be
/// decrypted into somewhere owned before a PipeReader can hand it out.
///
/// These need kTLS for the same reason the rest of the TLS suite does: without the module,
/// AcceptAsync cannot complete the handoff and there is no session to build a pipe over.
/// </summary>
internal static class TlsPipeTests
{
    public static void Register(Runner runner, bool ktls)
    {
        runner.Test("tls pipe: a request served entirely through TlsConnectionDualPipe", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            var options = new TlsOptions { CertificatePath = certPath, KeyPath = keyPath };

            int port = TestServer.Start(PipeHandler, r => TlsService.Start(r, options));

            (int status, string body) = Client.GetTls(port, "/");
            Assert.Equal(200, status);
            Assert.Equal("pipe-tls-ok", body);
        }, skip: !ktls);

        runner.Test("tls pipe: garbage after the handshake faults the reader, not a clean EOF", () =>
        {
            // The property both hand-rolled pumps get wrong. A TLS protocol error must reach the
            // reader as an exception; completing the pipe normally would make a corrupted or
            // truncated stream look exactly like the peer hanging up politely.
            (string certPath, string keyPath) = TestCert.Ensure();
            var options = new TlsOptions { CertificatePath = certPath, KeyPath = keyPath };

            var faulted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int port = TestServer.Start(FaultReportingHandler(faulted), r => TlsService.Start(r, options));

            Client.SendTlsGarbageAfterHandshake(port);

            Assert.True(faulted.Task.Wait(TimeSpan.FromSeconds(10)),
                "the reader should have observed a fault within 10 s");

            // Assert on WHAT failed, not merely that something did. The harness's own liveness
            // probe opens a raw TCP connection and fails the handshake on it, so "some exception
            // reached the handler" is satisfied before this test's garbage is even sent - which is
            // exactly how the first version of this test passed against every mutation.
            string reason = faulted.Task.Result;
            Assert.True(reason.Contains("TLS decrypt failed"),
                $"expected the decrypt to fault, got: {reason}");
        }, skip: !ktls);
    }

    // Serves one request off the decrypted PipeReader and answers as plaintext through the pipe's
    // writer, which is where kTLS takes over.
    private static async Task PipeHandler(Reactor reactor, TcpConnection connection)
    {
        TlsSession? session = null;
        TlsConnectionDualPipe? pipe = null;
        try
        {
            session = await reactor.GetService<TlsService>()!.AcceptAsync(connection);
            pipe = new TlsConnectionDualPipe(connection, session);

            ReadResult read = await pipe.Input.ReadAsync();
            pipe.Input.AdvanceTo(read.Buffer.End);

            const string body = "pipe-tls-ok";
            pipe.Output.Write(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
            await pipe.Output.FlushAsync();
        }
        catch
        {
            // The client hung up, or the handshake failed - either way there is nothing to serve.
        }
        finally
        {
            if (pipe is not null)
            {
                await pipe.DisposeAsync();   // disposes the session too
            }
            else
            {
                session?.Dispose();
            }
            connection.DecRef();
        }
    }

    // Reads until the pipe reports something, and publishes whether that was a fault or an EOF.
    private static Func<Reactor, TcpConnection, Task> FaultReportingHandler(TaskCompletionSource<string> faulted)
        => async (reactor, connection) =>
        {
            TlsSession? session = null;
            TlsConnectionDualPipe? pipe = null;
            try
            {
                // Outside the reporting try on purpose. The harness probes the port with a raw TCP
                // connection to learn the server is listening, which fails the handshake - and if
                // that counted as "the reader observed a fault" the test would pass without the
                // garbage ever arriving.
                try
                {
                    session = await reactor.GetService<TlsService>()!.AcceptAsync(connection);
                }
                catch
                {
                    return;
                }

                pipe = new TlsConnectionDualPipe(connection, session);

                while (true)
                {
                    ReadResult read = await pipe.Input.ReadAsync();
                    pipe.Input.AdvanceTo(read.Buffer.End);

                    if (read.IsCompleted)
                    {
                        // Clean completion - which for this test is the WRONG answer.
                        faulted.TrySetResult(string.Empty);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                faulted.TrySetResult(e.Message);
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync();
                }
                else
                {
                    session?.Dispose();
                }
                connection.DecRef();
            }
        };
}
