using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Http;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Orchestrates the metadata providers: asks only those capable of supplying what is missing, in the
/// admin's per-field order, and merges their partial answers into one record.
/// </summary>
public interface IMetadataAggregator
{
    /// <summary>
    /// Gathers the wanted fields from the configured providers.
    /// </summary>
    /// <param name="identity">The title's resolved ids.</param>
    /// <param name="wanted">The fields the caller is missing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The merged fragment, or null when nothing answered.</returns>
    Task<MetadataFragment?> FetchAsync(MediaIdentity identity, MetadataCapability wanted, CancellationToken cancellationToken);

    /// <summary>Per-provider health, for the diagnostics endpoint.</summary>
    /// <returns>The health snapshot, ordered by provider name.</returns>
    IReadOnlyList<ProviderHealth> Health();

    /// <summary>The names of providers that currently hold an API key.</summary>
    /// <returns>The configured provider names.</returns>
    IReadOnlyCollection<string> ConfiguredProviders();
}
