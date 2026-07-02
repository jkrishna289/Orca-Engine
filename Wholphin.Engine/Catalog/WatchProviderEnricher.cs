using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Catalog;

/// <summary>
/// Default <see cref="IWatchProviderEnricher"/> backed by <see cref="ITmdbClient"/>. Each pass takes a
/// batch of rows that have never been checked (<c>ProvidersSyncedAt == null</c>) and a known TMDB id,
/// resolves the primary streaming-provider brand for the configured region, stores it on the row, and
/// caches that provider's logo. Rows are stamped regardless of outcome so a title without a provider
/// rotates to the back instead of being re-fetched every pass.
/// </summary>
public class WatchProviderEnricher : IWatchProviderEnricher
{
    private static readonly MediaType[] Enrichable = { MediaType.Movie, MediaType.Series };

    private readonly ITmdbClient _tmdb;
    private readonly IProviderLogoCache _logos;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<WatchProviderEnricher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchProviderEnricher"/> class.
    /// </summary>
    public WatchProviderEnricher(
        ITmdbClient tmdb,
        IProviderLogoCache logos,
        IWholphinDbContextFactory factory,
        IEngineMetrics metrics,
        ILogger<WatchProviderEnricher> logger)
    {
        _tmdb = tmdb;
        _logos = logos;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> EnrichAsync(int maxItems = 40, CancellationToken ct = default)
    {
        if (!_tmdb.IsConfigured)
        {
            return 0;
        }

        var region = Plugin.Instance?.Configuration?.WatchProviderRegion;
        region = string.IsNullOrWhiteSpace(region) ? "US" : region!.Trim();

        var limit = Math.Clamp(maxItems, 1, 200);

        await using var db = _factory.Create();
        var candidates = await db.CatalogItems
            .Where(c => c.TmdbId != null && c.ProvidersSyncedAt == null)
            .OrderBy(c => c.LastSyncedAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var tagged = 0;
        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Stamp regardless of outcome so an untaggable title rotates to the back of the queue.
            item.ProvidersSyncedAt = DateTime.UtcNow;

            if (!Enrichable.Contains(item.MediaType))
            {
                continue;
            }

            var brand = await _tmdb.GetPrimaryProviderAsync(item.TmdbId!.Value, item.MediaType, region, ct).ConfigureAwait(false);
            if (brand is null)
            {
                continue;
            }

            item.ProviderBrandId = brand.ProviderId;
            item.ProviderBrandName = brand.ProviderName;
            await _logos.EnsureCachedAsync(brand.ProviderId, brand.LogoPath, ct).ConfigureAwait(false);
            tagged++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (tagged > 0)
        {
            _metrics.Increment("tmdb.providers.items", tagged);
        }

        _logger.LogInformation("Orca Engine: watch-provider enrichment tagged {Count} of {Scanned} rows.", tagged, candidates.Count);
        return tagged;
    }
}
