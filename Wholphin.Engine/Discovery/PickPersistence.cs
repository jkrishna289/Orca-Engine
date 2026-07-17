using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Home;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// Pipeline stage 6 — the only stage that writes. In one transaction: imports the selected
/// candidates' catalog rows (capped per pull), upserts the justification picks with per-kind TTLs
/// and real score columns, replaces stale global picks, updates recommendation memory, trims the
/// per-user pick caps, and records the run's funnel report. Afterwards it invalidates the caches
/// the writes made stale.
/// </summary>
public class PickPersistence
{
    /// <summary>Maximum live picks kept per user per kind (oldest beyond the cap are trimmed).</summary>
    public const int MaxLivePicksPerUserPerKind = 60;

    /// <summary>TTL for global (trending/country) picks — a safety net; each successful pull replaces them wholesale.</summary>
    public static readonly TimeSpan GlobalPickTtl = TimeSpan.FromDays(3);

    private readonly IWholphinDbContextFactory _factory;
    private readonly ICache _cache;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger<PickPersistence> _logger;

    /// <summary>Initializes a new instance of the <see cref="PickPersistence"/> class.</summary>
    /// <param name="factory">Database context factory.</param>
    /// <param name="cache">The L1 cache (home + content-vector invalidation).</param>
    /// <param name="embeddings">The embedding service (names the content-vector cache key).</param>
    /// <param name="logger">The logger.</param>
    public PickPersistence(
        IWholphinDbContextFactory factory,
        ICache cache,
        IEmbeddingService embeddings,
        ILogger<PickPersistence> logger)
    {
        _factory = factory;
        _cache = cache;
        _embeddings = embeddings;
        _logger = logger;
    }

    /// <summary>The outcome of the write stage.</summary>
    /// <param name="Imported">How many new catalog rows were created.</param>
    /// <param name="PicksWritten">How many picks were inserted or refreshed.</param>
    public sealed record PersistenceResult(int Imported, int PicksWritten);

    /// <summary>Persists a run's selected picks and its funnel report.</summary>
    /// <param name="context">The run context.</param>
    /// <param name="picks">The selected pick proposals.</param>
    /// <param name="report">The funnel report accumulated by the orchestrator.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The import/write counts.</returns>
    public async Task<PersistenceResult> PersistAsync(
        DiscoveryContext context,
        IReadOnlyList<SelectedPick> picks,
        DiscoveryRunReport report,
        CancellationToken ct = default)
    {
        var imported = 0;
        var written = 0;

        await using var db = _factory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 1) Resolve or import the catalog rows behind the picks (respecting the import cap).
        var tmdbIds = picks.Select(p => p.Scored.Candidate.Result.TmdbId).Distinct().ToList();
        var existing = await db.CatalogItems
            .Where(c => c.TmdbId != null && tmdbIds.Contains(c.TmdbId.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byKey = existing
            .GroupBy(c => (c.TmdbId!.Value, c.MediaType))
            .ToDictionary(g => g.Key, g => g.First());

        var persistable = new List<(SelectedPick Pick, CatalogItem Item)>();
        foreach (var pick in picks)
        {
            var result = pick.Scored.Candidate.Result;
            if (!byKey.TryGetValue((result.TmdbId, result.MediaType), out var item))
            {
                if (context.UserId != Guid.Empty && imported >= context.Tuning.MaxImportsPerPull)
                {
                    continue; // budget spent: existing rows may still be refreshed, new ones wait
                }

                item = new CatalogItem
                {
                    TmdbId = result.TmdbId,
                    MediaType = result.MediaType,
                    Availability = AvailabilityState.Request,
                    Title = result.Title,
                    Overview = result.Overview,
                    ProductionYear = result.Year,
                    CommunityRating = result.CommunityRating,
                    GenresJson = result.Genres.Count > 0 ? JsonSerializer.Serialize(result.Genres) : null,
                    OriginalLanguage = result.OriginalLanguage,
                    PosterImageUrl = result.PosterImageUrl,
                    BackdropImageUrl = result.BackdropImageUrl,
                    TrailerUrl = result.TrailerUrl,
                    LastSyncedAt = context.Now,
                };
                db.CatalogItems.Add(item);
                byKey[(result.TmdbId, result.MediaType)] = item;
                imported++;
            }

            persistable.Add((pick, item));
        }

        // Flush the imports so new rows carry real ids for the pick foreign keys.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // 2) Upsert picks. CreatedAt is deliberately NOT refreshed — it anchors "how long has this
        //    been shown", which drives the ignored-cycle decay in the sweep.
        var kinds = persistable.Select(p => p.Pick.Kind).Distinct().ToList();
        var itemIds = persistable.Select(p => p.Item.Id).ToList();
        var existingPicks = await db.UserDiscoveryPicks
            .Where(p => p.UserId == context.UserId && kinds.Contains(p.Kind) && itemIds.Contains(p.CatalogItemId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var picksByKey = existingPicks
            .GroupBy(p => (p.CatalogItemId, p.Kind))
            .ToDictionary(g => g.Key, g => g.First());

        var keptGlobalIds = new List<long>();
        foreach (var (pick, item) in persistable)
        {
            if (!picksByKey.TryGetValue((item.Id, pick.Kind), out var row))
            {
                row = new UserDiscoveryPick
                {
                    UserId = context.UserId,
                    CatalogItemId = item.Id,
                    Kind = pick.Kind,
                    CreatedAt = context.Now,
                };
                db.UserDiscoveryPicks.Add(row);
            }

            var score = pick.Scored.Score;
            row.SourceType = pick.Scored.Candidate.Attributions
                .OrderByDescending(a => a.SourceConfidence)
                .FirstOrDefault()?.SourceType;
            row.Reason = pick.Reason;
            row.SeedTmdbId = pick.SeedTmdbId;
            row.SeedTitle = pick.SeedTitle;
            row.Country = pick.Country;
            row.FinalScore = score.Final;
            row.TasteScore = score.Taste;
            row.PopularityScore = score.Popularity;
            row.FreshnessScore = score.Freshness;
            row.ScoreExplanationJson = JsonSerializer.Serialize(new
            {
                score.Novelty,
                score.CountryBoost,
                score.InterestMultiplier,
                score.ExposurePenalty,
                score.SourceConfidence,
                score.DiversityPenalty,
                Sources = pick.Scored.Candidate.Attributions,
            });
            row.ExpiresAt = context.Now + TtlFor(pick.Kind, context.Tuning);
            written++;

            if (context.UserId == Guid.Empty)
            {
                keptGlobalIds.Add(item.Id);
            }
        }

        // 3) Global runs replace their lists wholesale: stale globals of the run's kinds drop out.
        if (context.UserId == Guid.Empty && kinds.Count > 0)
        {
            var stale = await db.UserDiscoveryPicks
                .Where(p => p.UserId == Guid.Empty && kinds.Contains(p.Kind) && !keptGlobalIds.Contains(p.CatalogItemId))
                .Where(p => context.CountryCode == null ? p.Country == null : p.Country == context.CountryCode)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            db.UserDiscoveryPicks.RemoveRange(stale);
        }

        // 4) Recommendation memory: every persisted per-user pick counts as one more recommendation.
        if (context.UserId != Guid.Empty)
        {
            var memoryKeys = persistable
                .Select(p => (p.Pick.Scored.Candidate.Result.TmdbId, p.Pick.Scored.Candidate.Result.MediaType))
                .Distinct()
                .ToList();
            var memoryTmdbIds = memoryKeys.Select(k => k.TmdbId).ToList();
            var memories = await db.UserItemMemories
                .Where(m => m.UserId == context.UserId && memoryTmdbIds.Contains(m.TmdbId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var memoryByKey = memories.ToDictionary(m => (m.TmdbId, m.MediaType), m => m);
            foreach (var (tmdbId, mediaType) in memoryKeys)
            {
                if (!memoryByKey.TryGetValue((tmdbId, mediaType), out var memory))
                {
                    memory = new UserItemMemory
                    {
                        UserId = context.UserId,
                        TmdbId = tmdbId,
                        MediaType = mediaType,
                        InterestScore = 1.0,
                        UpdatedAt = context.Now,
                    };
                    db.UserItemMemories.Add(memory);
                }

                InterestModel.OnRecommended(memory, context.Now);
            }

            // 5) Trim the per-kind live-pick caps (oldest first).
            foreach (var kind in kinds)
            {
                var overflow = await db.UserDiscoveryPicks
                    .Where(p => p.UserId == context.UserId && p.Kind == kind)
                    .OrderByDescending(p => p.CreatedAt)
                    .Skip(MaxLivePicksPerUserPerKind)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                db.UserDiscoveryPicks.RemoveRange(overflow);
            }
        }

        // 6) The run's funnel report.
        report.Selected = picks.Count;
        db.DiscoveryRuns.Add(new DiscoveryRun
        {
            UserId = report.UserId,
            StartedAt = report.StartedAt,
            DurationMs = (long)(DateTime.UtcNow - report.StartedAt).TotalMilliseconds,
            Generated = report.Generated,
            FilteredOut = report.FilteredOut,
            FilterReasonsJson = report.FilterReasons.Count > 0
                ? JsonSerializer.Serialize(report.FilterReasons.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value))
                : null,
            Scored = report.Scored,
            DiversityReordered = report.DiversityReordered,
            BelowThreshold = report.BelowThreshold,
            Selected = report.Selected,
            Imported = imported,
            PerSourceJson = BuildPerSourceJson(report, picks),
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        // 7) Invalidate what the writes made stale: the user's precomputed home, and the
        //    content-vector snapshot when new rows joined the catalog.
        if (context.UserId != Guid.Empty)
        {
            _cache.Remove(HomeService.CacheKey(context.UserId, HomeService.DefaultRowSize, HomeService.DefaultCapabilities()));
        }

        if (imported > 0)
        {
            _cache.Remove($"contentvectors:{_embeddings.ActiveProviderName}");
        }

        _logger.LogInformation(
            "Orca Engine: discovery persisted {Picks} picks ({Imported} new catalog rows) for {Scope}.",
            written,
            imported,
            context.UserId == Guid.Empty ? (context.CountryCode is { } cc ? $"country {cc}" : "global") : $"user {context.UserId}");
        return new PersistenceResult(imported, written);
    }

    private static TimeSpan TtlFor(DiscoveryPickKind kind, DiscoveryTuning tuning) => kind switch
    {
        // Seeded picks age slowest (seeds change slowly); trending/country are weekly data with a
        // short safety TTL because each successful pull replaces them anyway.
        DiscoveryPickKind.BecauseYouWatched => TimeSpan.FromDays(tuning.PickTtlDays * 2),
        DiscoveryPickKind.Trending or DiscoveryPickKind.Country => GlobalPickTtl,
        DiscoveryPickKind.LlmPick => TimeSpan.FromDays(tuning.PickTtlDays),
        _ => TimeSpan.FromDays(tuning.PickTtlDays),
    };

    private static string? BuildPerSourceJson(DiscoveryRunReport report, IReadOnlyList<SelectedPick> picks)
    {
        if (report.GeneratedBySource.Count == 0)
        {
            return null;
        }

        var selectedBySource = picks
            .Select(p => p.Scored.Candidate.Attributions.OrderByDescending(a => a.SourceConfidence).FirstOrDefault()?.SourceType)
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s!)
            .ToDictionary(g => g.Key, g => g.Count());
        var merged = report.GeneratedBySource.ToDictionary(
            kv => kv.Key,
            kv => new { Generated = kv.Value, Selected = selectedBySource.GetValueOrDefault(kv.Key) });
        return JsonSerializer.Serialize(merged);
    }
}
