using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Llm;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Default <see cref="IContentWarningEnricher"/>. Each pass takes a batch of titles that have never had
/// warnings attempted (<c>ContentWarningsSyncedAt == null</c>) and calls <see cref="IContentWarningProvider"/>,
/// which computes via Groq and persists the result on the catalog row. Mirrors the watch-provider enricher.
/// </summary>
public class ContentWarningEnricher : IContentWarningEnricher
{
    private readonly IContentWarningProvider _provider;
    private readonly ILlmProvider _llm;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<ContentWarningEnricher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentWarningEnricher"/> class.
    /// </summary>
    public ContentWarningEnricher(
        IContentWarningProvider provider,
        ILlmProvider llm,
        IWholphinDbContextFactory factory,
        IEngineMetrics metrics,
        ILogger<ContentWarningEnricher> logger)
    {
        _provider = provider;
        _llm = llm;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    private static bool FeatureEnabled => Plugin.Instance?.Configuration?.FeatureContentWarnings == true;

    /// <inheritdoc />
    public async Task<int> EnrichAsync(int maxItems = 25, CancellationToken ct = default)
    {
        if (!_llm.IsConfigured || !FeatureEnabled)
        {
            return 0;
        }

        var limit = Math.Clamp(maxItems, 1, 100);

        // Only playable titles get warnings — they're shown at playback, and requestable items can't be
        // played. This focuses Groq usage on the ~dozens of available movies/series, not the whole catalog.
        await using var db = _factory.Create();
        var candidates = await db.CatalogItems
            .Where(c => c.ContentWarningsSyncedAt == null
                        && (c.Availability == AvailabilityState.WatchNow
                            || c.Availability == AvailabilityState.RecentlyAdded))
            .OrderBy(c => c.LastSyncedAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var processed = 0;
        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // GetAsync computes via Groq and persists ContentWarningsJson + stamps ContentWarningsSyncedAt.
            await _provider.GetAsync(item, ct).ConfigureAwait(false);
            processed++;
        }

        if (processed > 0)
        {
            _metrics.Increment("warnings.pregen.items", processed);
        }

        _logger.LogInformation("Orca Engine: content-warning pre-generation processed {Count} titles.", processed);
        return processed;
    }
}
