using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Discovery;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Ops + audit surface for taste-driven discovery: trigger pulls manually, inspect a user's live
/// picks (the "why is this shown" record), read the per-run funnel reports, and compare source
/// performance.
/// </summary>
[ApiController]
[Route("OrcaEngine/Discovery")]
[Produces("application/json")]
public class DiscoveryController : ControllerBase
{
    private readonly IDiscoveryOrchestrator _discovery;
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>Initializes a new instance of the <see cref="DiscoveryController"/> class.</summary>
    /// <param name="discovery">The discovery orchestrator.</param>
    /// <param name="factory">Database context factory (read-only audit queries).</param>
    public DiscoveryController(IDiscoveryOrchestrator discovery, IWholphinDbContextFactory factory)
    {
        _discovery = discovery;
        _factory = factory;
    }

    /// <summary>Runs one taste-driven pull for a user.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pull outcome.</returns>
    [HttpPost("Pull")]
    [AllowAnonymous]
    public async Task<ActionResult<TastePullResult>> Pull([FromQuery] Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest(new { error = "userId is required." });
        }

        return await _discovery.PullForUserAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the global pulls (trending + every configured country).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-list pick counts.</returns>
    [HttpPost("PullGlobal")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<GlobalPullResult>> PullGlobal(CancellationToken cancellationToken)
        => await _discovery.PullGlobalAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Runs the discovery maintenance sweep (decay, expiry, orphan GC, retention).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sweep counts.</returns>
    [HttpPost("Sweep")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<SweepResult>> Sweep(CancellationToken cancellationToken)
        => await _discovery.SweepAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Lists live discovery picks with their justification and score columns. Omit
    /// <paramref name="userId"/> for the global (trending/country) picks.
    /// </summary>
    /// <param name="userId">The Jellyfin user id, or empty for globals.</param>
    /// <param name="kind">Optional kind filter (TasteMatch, BecauseYouWatched, Trending, Country, Exploration).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The picks, strongest first.</returns>
    [HttpGet("Picks")]
    [AllowAnonymous]
    public async Task<IActionResult> Picks(
        [FromQuery] Guid userId,
        [FromQuery] string? kind,
        CancellationToken cancellationToken)
    {
        DiscoveryPickKind? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<DiscoveryPickKind>(kind, ignoreCase: true, out var parsed))
            {
                return BadRequest(new { error = $"Unknown kind '{kind}'." });
            }

            kindFilter = parsed;
        }

        var now = DateTime.UtcNow;
        await using var db = _factory.Create();
        var query =
            from pick in db.UserDiscoveryPicks.AsNoTracking()
            join item in db.CatalogItems.AsNoTracking() on pick.CatalogItemId equals item.Id
            where pick.UserId == userId && (pick.ExpiresAt == null || pick.ExpiresAt > now)
            select new { pick, item.Title, item.TmdbId, item.MediaType, item.Availability };
        if (kindFilter is { } kf)
        {
            query = query.Where(x => x.pick.Kind == kf);
        }

        var rows = await query
            .OrderByDescending(x => x.pick.FinalScore)
            .Take(500)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Ok(rows.Select(x => new
        {
            x.pick.Id,
            Kind = x.pick.Kind.ToString(),
            x.pick.Reason,
            x.pick.SourceType,
            x.Title,
            x.TmdbId,
            MediaType = x.MediaType.ToString(),
            Availability = x.Availability.ToString(),
            x.pick.SeedTitle,
            x.pick.Country,
            x.pick.FinalScore,
            x.pick.TasteScore,
            x.pick.PopularityScore,
            x.pick.FreshnessScore,

            // Written for every pick since schema v7 and never read until now. Carries novelty,
            // country boost, interest multiplier, exposure/diversity penalties and the full source
            // attribution list — the entire "why was this recommended" answer, already on disk.
            x.pick.ScoreExplanationJson,
            x.pick.CreatedAt,
            x.pick.ExpiresAt,
        }));
    }

    /// <summary>Lists recent pipeline runs with their funnel reports (generated → filtered → scored → selected).</summary>
    /// <param name="userId">The Jellyfin user id, or empty for global runs.</param>
    /// <param name="limit">Maximum runs returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The runs, newest first.</returns>
    [HttpGet("Runs")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> Runs(
        [FromQuery] Guid userId,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var db = _factory.Create();
        var runs = await db.DiscoveryRuns.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Ok(runs);
    }

    /// <summary>
    /// Per-source performance over the live picks: how many picks each source produced and how
    /// many of those the users actually engaged with (via recommendation memory).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One row per source.</returns>
    [HttpGet("SourceStats")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> SourceStats(CancellationToken cancellationToken)
    {
        await using var db = _factory.Create();
        var picks = await (
            from pick in db.UserDiscoveryPicks.AsNoTracking()
            join item in db.CatalogItems.AsNoTracking() on pick.CatalogItemId equals item.Id
            where item.TmdbId != null
            select new { pick.UserId, pick.SourceType, pick.Kind, pick.FinalScore, pick.CreatedAt, TmdbId = item.TmdbId!.Value, item.MediaType })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var memories = await db.UserItemMemories.AsNoTracking()
            .Where(m => m.LastEngagedAt != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var engagedByKey = memories
            .GroupBy(m => (m.UserId, m.TmdbId, m.MediaType))
            .ToDictionary(g => g.Key, g => g.Max(m => m.LastEngagedAt!.Value));

        var stats = picks
            .GroupBy(p => p.SourceType ?? "unknown")
            .Select(g => new
            {
                Source = g.Key,
                Picks = g.Count(),
                Engaged = g.Count(p => engagedByKey.TryGetValue((p.UserId, p.TmdbId, p.MediaType), out var at) && at >= p.CreatedAt),
                AvgFinalScore = Math.Round(g.Average(p => p.FinalScore), 4),
                ByKind = g.GroupBy(p => p.Kind.ToString()).ToDictionary(k => k.Key, k => k.Count()),
            })
            .OrderByDescending(s => s.Picks)
            .ToList();
        return Ok(stats);
    }
}
