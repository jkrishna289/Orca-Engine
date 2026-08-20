using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// A fail-soft port over one external metadata API. Implementations return PARTIAL data — a provider
/// declares what it can supply via <see cref="Capabilities"/> and is only ever asked for fields the
/// caller is actually missing.
/// </summary>
/// <remarks>
/// One narrow port with a capability flag, rather than the seven-method interface the design sketch
/// proposed: no provider here implements more than half of those, so the fat version would be five
/// <c>return null;</c> bodies per implementation for identical filtering behaviour.
/// </remarks>
public interface IMetadataProvider
{
    /// <summary>Gets the stable lowercase token this provider is named by in the priority settings.</summary>
    string Name { get; }

    /// <summary>Gets the fields this provider can supply at all.</summary>
    MetadataCapability Capabilities { get; }

    /// <summary>Gets a value indicating whether the provider is configured (has its API key). Unconfigured providers are never queried.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Fetches whatever this provider knows about a title.
    /// </summary>
    /// <param name="identity">The resolved cross-provider ids for the title.</param>
    /// <param name="wanted">The capabilities the caller is missing; a provider may ignore this and return everything it has.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A partial fragment, or null when the provider has nothing for this title.</returns>
    /// <remarks>Never throws into the caller: every failure degrades to null.</remarks>
    Task<MetadataFragment?> FetchAsync(MediaIdentity identity, MetadataCapability wanted, CancellationToken cancellationToken);
}
