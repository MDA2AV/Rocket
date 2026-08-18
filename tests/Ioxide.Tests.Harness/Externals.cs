using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ioxide;

namespace Ioxide.Tests;

// Optional things the tests drive but do not own: docker sidecars, and the two curl builds.
// Each reports ABSENCE rather than failing, so every suite stays runnable on a bare machine.

/// <summary>Reachability checks so sidecar-backed tests skip cleanly when the dependency is absent.</summary>
public static class Sidecars
{
    public static bool Reachable(string host, int port)
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(host, port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // kTLS needs the kernel's 'tls' module; the loaded module shows up here.
    public static bool KtlsAvailable() => Directory.Exists("/sys/module/tls");
}

/// <summary>
/// An HTTP/3 curl, used as a client that actually VALIDATES the server - which ioxide's own QUIC
/// client does not do, so nothing driven by it can answer "would a real client accept this".
/// </summary>
/// <remarks>
/// Optional by design: h3 support is not in a stock curl, so a test that needs one skips where it
/// is missing rather than failing. Set <c>IOXIDE_CURL_H3</c> to point at a build, otherwise
/// <c>curl-h3</c> and then <c>curl</c> are tried and each is asked whether it really has HTTP3.
/// </remarks>
public static class CurlH3
{
    private static readonly Lazy<string?> Found = new(Locate);

    /// <summary>The binary to run, or null when this machine has no HTTP/3 curl.</summary>
    public static string? Path => Found.Value;

    public static bool Available => Found.Value is not null;

    private static string? Locate()
    {
        string? configured = Environment.GetEnvironmentVariable("IOXIDE_CURL_H3");

        foreach (string candidate in configured is null
                     ? ["curl-h3", "curl"]
                     : new[] { configured, "curl-h3", "curl" })
        {
            if (HasHttp3(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasHttp3(string binary)
    {
        try
        {
            (int exit, string stdout, _) = Run(binary, ["--version"], timeoutMs: 5000);

            // The Features line is the only claim that counts - a curl can link ngtcp2 and still
            // be built without the protocol.
            return exit == 0 && stdout.Contains("HTTP3");
        }
        catch (Exception)
        {
            return false;   // not on PATH, not executable: simply not a curl we can use
        }
    }

    /// <summary>Runs curl and hands back what it said. Never throws on a non-zero exit - a refused
    /// connection IS the result some tests are asserting on.</summary>
    public static (int Exit, string Stdout, string Stderr) Run(string binary, string[] args, int timeoutMs = 15000)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new IOException($"could not start {binary}");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{binary} did not exit within {timeoutMs}ms");
        }

        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// One verified HTTP/3 GET. <paramref name="host"/> is what curl asks for by name - sent as SNI
    /// AND checked against the certificate it gets back - while the connection still goes to
    /// loopback, which is what --resolve is for.
    /// </summary>
    public static (int Exit, string Stdout, string Stderr) Get(string host, int port, string caPath, string path = "/")
        => Run(Path!,
        [
            "--http3-only",
            "--cacert", caPath,
            "--resolve", $"{host}:{port}:127.0.0.1",
            "--max-time", "15",
            "--silent", "--show-error",
            $"https://{host}:{port}{path}",
        ]);
}

/// <summary>
/// The system curl, over TCP - a client that validates the chain AND matches the name, which is
/// what makes it worth driving alongside SslStream.
/// </summary>
/// <remarks>
/// SslStream in this harness accepts any certificate, so it can say WHICH one arrived but never
/// whether anyone would take it. curl answers the second question, and it is the one that matters
/// while certificates are being replaced underneath it.
/// </remarks>
public static class Curl
{
    private static readonly Lazy<bool> Present = new(() =>
    {
        try
        {
            return CurlH3.Run("curl", ["--version"], timeoutMs: 5000).Exit == 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public static bool Available => Present.Value;

    /// <summary>
    /// One verified HTTPS GET. <paramref name="host"/> is sent as SNI and checked against the
    /// certificate; the connection still goes to loopback, which is what --resolve is for.
    /// Optionally presents a client certificate, for a server that asks for one.
    /// </summary>
    public static (int Exit, string Stdout, string Stderr) Get(string host, int port, string caPath,
        string path = "/", string? clientCert = null, string? clientKey = null, int timeoutMs = 15000)
    {
        var args = new List<string>
        {
            "--cacert", caPath,
            "--resolve", $"{host}:{port}:127.0.0.1",
            "--max-time", "15",
            "--silent", "--show-error",
        };

        if (clientCert is not null && clientKey is not null)
        {
            args.Add("--cert");
            args.Add(clientCert);
            args.Add("--key");
            args.Add(clientKey);
        }

        args.Add($"https://{host}:{port}{path}");

        return CurlH3.Run("curl", [.. args], timeoutMs);
    }
}
