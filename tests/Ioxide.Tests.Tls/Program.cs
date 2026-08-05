namespace Ioxide.Tests;

/// <summary>
/// TLS termination: the OpenSSL handshake driven over the ring, then kernel-TLS transmit. Needs
/// the Linux 'tls' module, which does not survive a reboot and does not autoload for an
/// unprivileged process, so it is loaded once per boot:
///
///     sudo modprobe tls
///
/// The suite skips when the module is absent rather than reporting a failure.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var runner = new Runner();
        bool ktls = Sidecars.KtlsAvailable();
        Console.WriteLine($"kTLS {(ktls ? "available" : "absent - sudo modprobe tls")}\n");

        TlsTests.Register(runner, ktls);
        DecryptFaultTests.Register(runner, ktls);

        return runner.Summary();
    }
}
