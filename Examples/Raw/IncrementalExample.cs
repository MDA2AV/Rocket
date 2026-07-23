using ioxide;

namespace Examples.Raw;

/// <summary>Same handler as the shared raw example; only the config differs (kernel 6.12+).</summary>
public static class IncrementalExample
{
    public static Task Handle(Reactor r, TcpConnection conn) => SharedExample.Handle(r, conn);
}
