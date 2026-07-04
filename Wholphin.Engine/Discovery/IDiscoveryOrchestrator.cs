using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// The discovery pipeline's public port: taste-driven per-user pulls, global (trending/country)
/// pulls, and the maintenance sweep. Consumed by the maintenance worker and the dev/ops
/// controller; everything is gated + fail-soft, so calls are harmless when the feature is off or
/// TMDB is unconfigured.
/// </summary>
public interface IDiscoveryOrchestrator
{
    /// <summary>
    /// Runs one taste-driven pull for a user: gathers candidates from the per-user sources,
    /// filters/scores/diversifies/selects them, imports the winners and writes their
    /// justification picks. Skips (without fallback) for cold profiles — cold-start users see
    /// only the justified global rows.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The pull outcome.</returns>
    Task<TastePullResult> PullForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Runs the global pulls: TMDB weekly trending plus one country pull per distinct configured
    /// country (per-user <c>pref.country</c> values and the admin region). Global picks are
    /// stored under <see cref="Guid.Empty"/> and replaced wholesale on each successful pull.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The per-list pick counts.</returns>
    Task<GlobalPullResult> PullGlobalAsync(CancellationToken ct = default);

    /// <summary>
    /// Maintenance sweep: applies the ignored-cycle interest decay to stale unengaged picks,
    /// deletes expired picks, garbage-collects orphaned external catalog rows (the no-firehose
    /// guarantee), and prunes old memory/run rows.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The sweep counts.</returns>
    Task<SweepResult> SweepAsync(CancellationToken ct = default);
}

/// <summary>The outcome of a per-user taste pull.</summary>
/// <param name="Skipped">Whether the pull was skipped (gates/cold profile).</param>
/// <param name="SkipReason">Why it was skipped (e.g. "disabled", "tmdb", "confidence").</param>
/// <param name="Imported">How many new catalog rows were imported.</param>
/// <param name="Picks">How many picks were written or refreshed.</param>
public sealed record TastePullResult(bool Skipped, string? SkipReason, int Imported, int Picks);

/// <summary>The outcome of the global pulls.</summary>
/// <param name="TrendingPicks">How many trending picks were written.</param>
/// <param name="CountryPicks">Picks written per country code.</param>
public sealed record GlobalPullResult(int TrendingPicks, IReadOnlyDictionary<string, int> CountryPicks);

/// <summary>The outcome of a maintenance sweep.</summary>
/// <param name="ExpiredPicks">Picks removed (expired or decayed out).</param>
/// <param name="DecayedItems">Memory records that took an ignored-cycle decay.</param>
/// <param name="OrphanRowsDeleted">Unjustified external catalog rows garbage-collected.</param>
public sealed record SweepResult(int ExpiredPicks, int DecayedItems, int OrphanRowsDeleted);
