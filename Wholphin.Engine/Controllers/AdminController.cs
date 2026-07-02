using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Operational diagnostics for the engine (Milestone 3 — Admin &amp; Operations). Surfaces a single
/// snapshot of catalog/behavior/profile/integration state so the dashboard (and ops) can see at a
/// glance whether the engine is healthy and configured.
/// </summary>
[ApiController]
[Route("OrcaEngine/Admin")]
[Produces("application/json")]
public class AdminController : ControllerBase
{
    private readonly IWholphinDbContextFactory _factory;
    private readonly ISettingsService _settings;
    private readonly IJellyseerrClient _jellyseerr;
    private readonly Integrations.Tmdb.ITmdbClient _tmdb;
    private readonly Integrations.Arr.IArrClient _arr;
    private readonly IEngineMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="factory">The database context factory.</param>
    /// <param name="settings">The layered settings resolver.</param>
    /// <param name="jellyseerr">The Jellyseerr client.</param>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="arr">The Sonarr/Radarr client.</param>
    /// <param name="metrics">Operational metrics.</param>
    public AdminController(IWholphinDbContextFactory factory, ISettingsService settings, IJellyseerrClient jellyseerr, Integrations.Tmdb.ITmdbClient tmdb, Integrations.Arr.IArrClient arr, IEngineMetrics metrics)
    {
        _factory = factory;
        _settings = settings;
        _jellyseerr = jellyseerr;
        _tmdb = tmdb;
        _arr = arr;
        _metrics = metrics;
    }

    /// <summary>Returns a full operational status snapshot of the engine.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status snapshot.</returns>
    [HttpGet("Status")]
    [AllowAnonymous]
    public async Task<ActionResult<AdminStatus>> GetStatus(CancellationToken cancellationToken)
    {
        var settings = await _settings.ResolveAsync(null, cancellationToken).ConfigureAwait(false);

        await using var db = _factory.Create();

        // Small catalog: project the two facets and group in memory.
        var facets = await db.CatalogItems
            .Select(c => new { c.MediaType, c.Availability })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var catalog = new CatalogSummary { Total = facets.Count };
        foreach (var group in facets.GroupBy(f => f.MediaType))
        {
            catalog.ByType[group.Key.ToString()] = group.Count();
        }

        foreach (var group in facets.GroupBy(f => f.Availability))
        {
            catalog.ByAvailability[group.Key.ToString()] = group.Count();
        }

        var behaviorEvents = await db.BehaviorEvents.CountAsync(cancellationToken).ConfigureAwait(false);
        var profiles = await db.UserProfiles.CountAsync(cancellationToken).ConfigureAwait(false);
        var roamingSettings = await db.UserSettings.CountAsync(cancellationToken).ConfigureAwait(false);

        return new AdminStatus
        {
            Plugin = Plugin.Instance?.Name ?? "Orca Engine",
            Version = Plugin.Instance?.Version?.ToString() ?? "unknown",
            SchemaVersion = Data.DatabaseInitializer.SchemaVersion,
            ServerTimeUtc = DateTime.UtcNow,
            UptimeMinutes = Math.Round((DateTime.UtcNow - Plugin.StartedUtc).TotalMinutes, 1),
            Enabled = settings.Enabled,
            Features = settings.Features,
            Catalog = catalog,
            BehaviorEvents = behaviorEvents,
            Profiles = profiles,
            RoamingSettings = roamingSettings,
            JellyseerrConfigured = _jellyseerr.IsConfigured,
            TmdbConfigured = _tmdb.IsConfigured,
            ArrConfigured = _arr.IsConfigured,
            Metrics = _metrics.Snapshot(),
        };
    }

    /// <summary>Returns the in-process operational metric counters.</summary>
    /// <returns>The counter snapshot.</returns>
    [HttpGet("Metrics")]
    [AllowAnonymous]
    public ActionResult<IReadOnlyDictionary<string, long>> GetMetrics() => Ok(_metrics.Snapshot());

    /// <summary>Resets all operational metric counters.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Metrics/Reset")]
    [AllowAnonymous]
    public ActionResult ResetMetrics()
    {
        _metrics.Reset();
        return NoContent();
    }

    /// <summary>Returns the most recent engine-proxied requests (durable request log).</summary>
    /// <param name="limit">Maximum rows to return (1-200, default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recent requests, newest first.</returns>
    [HttpGet("Requests")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<MediaRequest>>> GetRequests(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var db = _factory.Create();
        var rows = await db.MediaRequests.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Ok(rows);
    }

    /// <summary>Operational status snapshot.</summary>
    public class AdminStatus
    {
        /// <summary>Gets or sets the plugin name.</summary>
        public string Plugin { get; set; } = string.Empty;

        /// <summary>Gets or sets the plugin version.</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Gets or sets the engine schema version.</summary>
        public int SchemaVersion { get; set; }

        /// <summary>Gets or sets the current server time (UTC).</summary>
        public DateTime ServerTimeUtc { get; set; }

        /// <summary>Gets or sets minutes since the plugin loaded.</summary>
        public double UptimeMinutes { get; set; }

        /// <summary>Gets or sets a value indicating whether the engine is enabled.</summary>
        public bool Enabled { get; set; }

        /// <summary>Gets or sets the resolved (global) feature flags.</summary>
        public FeatureFlags Features { get; set; } = new();

        /// <summary>Gets or sets the catalog summary.</summary>
        public CatalogSummary Catalog { get; set; } = new();

        /// <summary>Gets or sets the total behavior-event count.</summary>
        public int BehaviorEvents { get; set; }

        /// <summary>Gets or sets the number of personalization profiles.</summary>
        public int Profiles { get; set; }

        /// <summary>Gets or sets the number of stored roaming settings.</summary>
        public int RoamingSettings { get; set; }

        /// <summary>Gets or sets a value indicating whether Jellyseerr is configured.</summary>
        public bool JellyseerrConfigured { get; set; }

        /// <summary>Gets or sets a value indicating whether a TMDB API key is configured.</summary>
        public bool TmdbConfigured { get; set; }

        /// <summary>Gets or sets a value indicating whether Sonarr or Radarr is configured.</summary>
        public bool ArrConfigured { get; set; }

        /// <summary>Gets or sets the in-process operational metric counters.</summary>
        public IReadOnlyDictionary<string, long> Metrics { get; set; } = new Dictionary<string, long>();
    }

    /// <summary>Catalog counts, total and by facet.</summary>
    public class CatalogSummary
    {
        /// <summary>Gets or sets the total catalog item count.</summary>
        public int Total { get; set; }

        /// <summary>Gets the per-media-type counts.</summary>
        public Dictionary<string, int> ByType { get; } = new();

        /// <summary>Gets the per-availability-state counts.</summary>
        public Dictionary<string, int> ByAvailability { get; } = new();
    }
}
