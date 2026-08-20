using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Http;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Default <see cref="IMetadataAggregator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The point of this class is that adding a provider does NOT mean every request calls every
/// provider. A provider is asked only when it is configured, only when it declares a capability the
/// caller is actually missing, and only until the missing set is empty.
/// </para>
/// <para>
/// ponytail: providers are queried sequentially with early exit rather than fanned out in parallel.
/// The only caller is a batched background enricher where latency is irrelevant, and the
/// latency-sensitive path (trailers) already has its own bounded priority queue. Fan out here if a
/// synchronous on-demand caller ever appears.
/// </para>
/// </remarks>
public class MetadataAggregator : IMetadataAggregator
{
    /// <summary>Every field group the merge layer resolves independently.</summary>
    private static readonly MetadataCapability[] FieldGroups =
    {
        MetadataCapability.Core,
        MetadataCapability.Artwork,
        MetadataCapability.Logo,
        MetadataCapability.Ratings,
        MetadataCapability.Ids,
        MetadataCapability.Trailer,
        MetadataCapability.Episodes,
    };

    private readonly IReadOnlyList<IMetadataProvider> _providers;
    private readonly IProviderGate _gate;
    private readonly ICache _cache;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<MetadataAggregator> _logger;
    private readonly Func<PluginConfiguration?> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataAggregator"/> class.
    /// </summary>
    /// <param name="providers">Every registered metadata provider.</param>
    /// <param name="gate">The shared provider gate (for the health snapshot).</param>
    /// <param name="cache">The L1 cache.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Configuration accessor; defaults to the live plugin configuration.</param>
    public MetadataAggregator(
        IEnumerable<IMetadataProvider> providers,
        IProviderGate gate,
        ICache cache,
        IEngineMetrics metrics,
        ILogger<MetadataAggregator> logger,
        Func<PluginConfiguration?>? config = null)
    {
        _providers = providers.ToList();
        _gate = gate;
        _cache = cache;
        _metrics = metrics;
        _logger = logger;
        _config = config ?? (() => Plugin.Instance?.Configuration);
    }

    /// <inheritdoc />
    public async Task<MetadataFragment?> FetchAsync(MediaIdentity identity, MetadataCapability wanted, CancellationToken cancellationToken)
    {
        if (wanted == MetadataCapability.None || !identity.HasAnyId)
        {
            return null;
        }

        var config = _config();
        var priority = MetadataPriority.FromConfig(config);
        var language = string.IsNullOrWhiteSpace(config?.MetadataLanguage) ? "en" : config!.MetadataLanguage.Trim();
        var positiveTtl = TimeSpan.FromDays(Math.Clamp(config?.MetadataCacheDays ?? 14, 1, 365));

        // A provider answering "nothing" is worth remembering, but for far less long than a real
        // answer: a title with no logo today may have one next week.
        var negativeTtl = TimeSpan.FromDays(1);

        var candidates = Candidates(wanted, priority).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var fragments = new List<MetadataFragment?>();
        var remaining = wanted;

        foreach (var provider in candidates)
        {
            if (remaining == MetadataCapability.None)
            {
                _metrics.Increment("metadata.aggregate.early_exit");
                break;
            }

            // Nothing this provider offers is still missing — skip it rather than spend the call.
            if ((provider.Capabilities & remaining) == MetadataCapability.None)
            {
                continue;
            }

            var fragment = await SingleFlight.GetOrCreateAsync(
                _cache,
                $"meta:{provider.Name}:{identity.CacheKey}",
                positiveTtl,
                negativeTtl,
                ct => provider.FetchAsync(identity, remaining, ct),
                cancellationToken).ConfigureAwait(false);

            if (fragment is null)
            {
                continue;
            }

            fragments.Add(fragment);
            remaining &= ~Satisfied(fragment);
        }

        if (fragments.Count == 0)
        {
            _metrics.Increment("metadata.aggregate.empty");
            return null;
        }

        _metrics.Increment("metadata.aggregate.ok");
        _logger.LogDebug(
            "Orca Engine: metadata aggregated from {Count} provider(s) for tmdb {TmdbId}.",
            fragments.Count,
            identity.TmdbId);

        return MetadataMerge.Merge(fragments, priority, language);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderHealth> Health() => _gate.Snapshot(ConfiguredProviders());

    /// <inheritdoc />
    public IReadOnlyCollection<string> ConfiguredProviders()
        => _providers.Where(p => p.IsConfigured).Select(p => p.Name).ToList();

    /// <summary>
    /// The providers worth asking, ordered so the admin's highest-priority source for the most
    /// important missing field goes first.
    /// </summary>
    /// <param name="wanted">The missing fields.</param>
    /// <param name="priority">The per-field order.</param>
    /// <returns>The ordered candidates.</returns>
    private IEnumerable<IMetadataProvider> Candidates(MetadataCapability wanted, MetadataPriority priority)
        => _providers
            .Where(p => p.IsConfigured && (p.Capabilities & wanted) != MetadataCapability.None)
            .OrderBy(p => BestRank(p, wanted, priority))
            .ThenBy(p => p.Name, StringComparer.Ordinal);

    /// <summary>A provider's best (lowest) rank across the field groups still wanted.</summary>
    private static int BestRank(IMetadataProvider provider, MetadataCapability wanted, MetadataPriority priority)
    {
        var best = int.MaxValue;
        foreach (var group in FieldGroups)
        {
            if ((wanted & group) == MetadataCapability.None || (provider.Capabilities & group) == MetadataCapability.None)
            {
                continue;
            }

            best = Math.Min(best, priority.Rank(group, provider.Name));
        }

        return best;
    }

    /// <summary>Which field groups a fragment actually filled — what lets the loop exit early.</summary>
    /// <param name="fragment">The fragment.</param>
    /// <returns>The satisfied capabilities.</returns>
    internal static MetadataCapability Satisfied(MetadataFragment fragment)
    {
        var satisfied = MetadataCapability.None;

        if (!string.IsNullOrWhiteSpace(fragment.Overview) && fragment.Genres.Count > 0)
        {
            satisfied |= MetadataCapability.Core;
        }

        if (fragment.Poster is not null && fragment.Backdrop is not null)
        {
            satisfied |= MetadataCapability.Artwork;
        }

        if (fragment.Logo is not null)
        {
            satisfied |= MetadataCapability.Logo;
        }

        if (fragment.Ratings.Count > 0)
        {
            satisfied |= MetadataCapability.Ratings;
        }

        if (!string.IsNullOrWhiteSpace(fragment.ImdbId))
        {
            satisfied |= MetadataCapability.Ids;
        }

        if (!string.IsNullOrWhiteSpace(fragment.TrailerUrl))
        {
            satisfied |= MetadataCapability.Trailer;
        }

        return satisfied;
    }
}
