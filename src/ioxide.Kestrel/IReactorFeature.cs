using ioxide;

namespace ioxide.Kestrel;

/// <summary>
/// Exposes the ioxide <see cref="Reactor"/> that owns the current connection. The transport sets this on
/// the connection's feature collection, so an endpoint can reach the connection's reactor (and its
/// ring-native services like PgPool / AssetReader) regardless of which thread Kestrel ran the endpoint on.
/// Prefer the <c>HttpContext.OnReactor(...)</c> helper, which uses this to run work on the reactor.
/// </summary>
public interface IReactorFeature
{
    Reactor Reactor { get; }
}
