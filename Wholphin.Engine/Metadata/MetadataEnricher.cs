using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Catalog;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// The multi-provider half of catalog enrichment: resolves external ids, then fills the fields TMDB
/// alone never could — critic ratings and clear logos — plus anything TMDB simply missed.
/// </summary>
/// <remarks>
/// A SECOND <see cref="ICatalogEnricher"/> alongside <see cref="TmdbEnricher"/> rather than a
/// replacement, because their candidate queries genuinely differ: TmdbEnricher pages rows lacking
/// genres or art against an effectively unlimited TMDB quota, while this one pages by
/// <see cref="CatalogItem.MetadataSyncedAt"/> against OMDb's 1000 requests a day. One merged query
/// would serve neither well.
/// </remarks>
public class MetadataEnricher : ICatalogEnricher
{
    private static readonly MediaType[] Enrichable = { MediaType.Movie, MediaType.Series };

    private readonly IMetadataAggregator _aggregator;
    private readonly IMediaIdentityResolver _identities;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<MetadataEnricher> _logger;
    private readonly Func<PluginConfiguration?> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataEnricher"/> class.
    /// </summary>
    /// <param name="aggregator">The provider aggregator.</param>
    /// <param name="identities">The external-id resolver.</param>
    /// <param name="factory">The database context factory.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Configuration accessor; defaults to the live plugin configuration.</param>
    public MetadataEnricher(
        IMetadataAggregator aggregator,
        IMediaIdentityResolver identities,
        IWholphinDbContextFactory factory,
        IEngineMetrics metrics,
        ILogger<MetadataEnricher> logger,
        Func<PluginConfiguration?>? config = null)
    {
        _aggregator = aggregator;
        _identities = identities;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
        _config = config ?? (() => Plugin.Instance?.Configuration);
    }

    /// <inheritdoc />
    public async Task<int> EnrichAsync(int maxItems = 50, CancellationToken ct = default)
    {
        var config = _config();
        if (config?.FeatureMetadataProviders == false)
        {
            return 0;
        }

        // Nothing beyond TMDB configured means TmdbEnricher already covers everything this could do,
        // so the whole pass is skipped rather than spending a database page to learn that.
        var configured = _aggregator.ConfiguredProviders();
        if (!configured.Any(name => !string.Equals(name, "tmdb", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        // Capped by the provider budget as well as the caller's request: the shared maintenance loop
        // asks every enricher for the same batch size, but this one spends a metered free tier.
        var budget = Math.Clamp(config?.MetadataItemsPerPass ?? 10, 1, 500);
        var limit = Math.Min(Math.Clamp(maxItems, 1, 500), budget);
        var staleBefore = DateTime.UtcNow - TimeSpan.FromDays(Math.Clamp(config?.MetadataRefreshDays ?? 30, 1, 3650));

        await using var db = _factory.Create();
        var candidates = await db.CatalogItems
            .Where(c => c.TmdbId != null
                        && (c.MetadataSyncedAt == null || c.MetadataSyncedAt < staleBefore))
            .OrderBy(c => c.MetadataSyncedAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var enriched = 0;
        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!Enrichable.Contains(item.MediaType))
            {
                // Nothing will ever enrich this type; stamp it so it stops being a candidate.
                item.MetadataSyncedAt = DateTime.UtcNow;
                continue;
            }

            var identity = await _identities.ResolveAsync(item, ct).ConfigureAwait(false);

            var wanted = MissingCapabilities(item);
            if (wanted == MetadataCapability.None)
            {
                item.MetadataSyncedAt = DateTime.UtcNow;
                continue;
            }

            var refusalsBefore = Refusals();
            var fragment = await _aggregator.FetchAsync(identity, wanted, ct).ConfigureAwait(false);

            // A provider that was rate-limited or short-circuited never actually looked at this title.
            // Stamping it anyway would hide the row for the whole refresh window over a temporary
            // quota problem — the exact failure that makes a metered free tier dangerous.
            if (Refusals() > refusalsBefore)
            {
                _metrics.Increment("metadata.enrich.deferred");
                continue;
            }

            // Stamped on a genuine attempt regardless of outcome, so a title no provider can enrich
            // rotates to the back instead of being re-fetched every pass — the guard TmdbEnricher uses.
            item.MetadataSyncedAt = DateTime.UtcNow;

            if (fragment is not null && Apply(item, fragment))
            {
                enriched++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (enriched > 0)
        {
            _metrics.Increment("metadata.enrich.items", enriched);
        }

        _logger.LogInformation(
            "Orca Engine: multi-provider metadata filled {Count} of {Scanned} candidate rows.",
            enriched,
            candidates.Count);
        return enriched;
    }

    /// <summary>
    /// How many provider calls have been refused outright — rate-limited, or turned away by an open
    /// circuit. A rise across one row means that row was never genuinely evaluated.
    /// </summary>
    private long Refusals()
    {
        long total = 0;
        foreach (var health in _aggregator.Health())
        {
            total += health.RateLimited + health.ShortCircuited;
        }

        return total;
    }

    /// <summary>Which field groups this row is still missing — the set the aggregator gets asked for.</summary>
    /// <param name="item">The catalog row.</param>
    /// <returns>The missing capabilities.</returns>
    internal static MetadataCapability MissingCapabilities(CatalogItem item)
    {
        var missing = MetadataCapability.None;

        // Language is in Core and library rows never get one from Jellyfin, so without it in this
        // condition a row with an overview and genres looked complete and was never asked again.
        if (string.IsNullOrWhiteSpace(item.Overview)
            || string.IsNullOrWhiteSpace(item.GenresJson)
            || string.IsNullOrWhiteSpace(item.OriginalLanguage))
        {
            missing |= MetadataCapability.Core;
        }

        // Library rows resolve their own art from the Jellyfin id, so only requestable rows need it.
        if (item.JellyfinItemId is null
            && (string.IsNullOrWhiteSpace(item.PosterImageUrl) || string.IsNullOrWhiteSpace(item.BackdropImageUrl)))
        {
            missing |= MetadataCapability.Artwork;
        }

        if (string.IsNullOrWhiteSpace(item.LogoImageUrl))
        {
            missing |= MetadataCapability.Logo;
        }

        if (string.IsNullOrWhiteSpace(item.RatingsJson))
        {
            missing |= MetadataCapability.Ratings;
        }

        return missing;
    }

    /// <summary>
    /// Writes a merged fragment onto the row, never downgrading a field a better-ranked provider
    /// already supplied.
    /// </summary>
    /// <param name="item">The catalog row, mutated in place.</param>
    /// <param name="fragment">The merged fragment.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// The merge layer picks the best CANDIDATE; this decides whether that beats what is already
    /// stored. Without the provenance check a later pass whose best provider was down would quietly
    /// replace a Fanart logo with a worse one.
    /// </remarks>
    internal static bool Apply(CatalogItem item, MetadataFragment fragment)
    {
        var sources = ReadSources(item.MetadataSourcesJson);
        var changed = false;

        if (fragment.Poster is { } poster && string.IsNullOrWhiteSpace(item.PosterImageUrl) && item.JellyfinItemId is null)
        {
            item.PosterImageUrl = poster.Url;
            sources["Poster"] = fragment.Source;
            changed = true;
        }

        if (fragment.Backdrop is { } backdrop && string.IsNullOrWhiteSpace(item.BackdropImageUrl) && item.JellyfinItemId is null)
        {
            item.BackdropImageUrl = backdrop.Url;
            sources["Backdrop"] = fragment.Source;
            changed = true;
        }

        if (fragment.Logo is { } logo && string.IsNullOrWhiteSpace(item.LogoImageUrl))
        {
            item.LogoImageUrl = logo.Url;
            sources["Logo"] = fragment.Source;
            changed = true;
        }

        if (fragment.Ratings.Count > 0 && string.IsNullOrWhiteSpace(item.RatingsJson))
        {
            item.RatingsJson = JsonSerializer.Serialize(fragment.Ratings);
            sources["Ratings"] = "omdb";
            changed = true;

            // CriticRating is the field the existing client DTOs already carry, so populating it from
            // Rotten Tomatoes lights up the UI with no contract change. Jellyfin's scale is 0-10.
            if (item.CriticRating is null && BestCritic(fragment.Ratings) is { } critic)
            {
                item.CriticRating = (float)Math.Round(critic / 10d, 1);
            }
        }

        if (string.IsNullOrWhiteSpace(item.Overview) && !string.IsNullOrWhiteSpace(fragment.Overview))
        {
            item.Overview = fragment.Overview;
            changed = true;
        }

        // Drives the Language affinity dimension and the embedding document. It was being fetched
        // and merged already; only the write back to the row was missing.
        if (string.IsNullOrWhiteSpace(item.OriginalLanguage) && !string.IsNullOrWhiteSpace(fragment.OriginalLanguage))
        {
            item.OriginalLanguage = fragment.OriginalLanguage;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(item.GenresJson) && fragment.Genres.Count > 0)
        {
            item.GenresJson = JsonSerializer.Serialize(fragment.Genres);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(item.TrailerUrl) && !string.IsNullOrWhiteSpace(fragment.TrailerUrl))
        {
            item.TrailerUrl = fragment.TrailerUrl;
            changed = true;
        }

        if (changed)
        {
            item.MetadataSourcesJson = JsonSerializer.Serialize(sources);
        }

        return changed;
    }

    /// <summary>Rotten Tomatoes first, then Metacritic, then IMDb — most-recognised critic score first.</summary>
    private static double? BestCritic(IReadOnlyDictionary<string, double> ratings)
    {
        foreach (var key in new[] { "rt", "metacritic", "imdb" })
        {
            if (ratings.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ReadSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Corrupt provenance must not stop enrichment; the worst case is re-recording it.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
