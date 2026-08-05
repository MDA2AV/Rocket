using System.Runtime.CompilerServices;

// The Alt-Svc parser is internal but security-relevant, so the e2e suite tests it directly.
[assembly: InternalsVisibleTo("Ioxide.Tests.Http")]
[assembly: InternalsVisibleTo("Ioxide.Tests.Unit")]

namespace ioxide.httpclient;

/// <summary>Any HTTP/3 client failure: connect, handshake, stream, or a protocol error.</summary>
public sealed class Http3ClientException : Exception
{
    public Http3ClientException(string message) : base(message) { }
}
