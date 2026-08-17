namespace Ioxide.Tests;

/// <summary>
/// TLS termination. The default configuration is OpenSSL in both directions and needs no kernel
/// module, so most of the suite runs anywhere. The kTLS TX opt-in tests need the Linux 'tls'
/// module, which does not survive a reboot and does not autoload for an unprivileged process, so
/// it is loaded once per boot:
///
///     sudo modprobe tls
///
/// Those tests skip when the module is absent rather than reporting a failure.
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
        TlsPipeTests.Register(runner, ktls);
        MutualTlsTests.Register(runner, ktls);
        SniTests.Register(runner, ktls);

        return runner.Summary();
    }
}
