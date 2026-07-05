using System;
using System.Collections.Generic;
using System.Threading;

namespace Wholphin.Engine.Intelligence;

/// <summary>
/// Computes a user's per-series watch state (<see cref="SeriesUserState"/>) live from Jellyfin's
/// per-episode user data. Watch state lives in Jellyfin, not the engine catalog, so this reads it
/// on demand (the home read-models that consume it are themselves cached/precomputed).
/// </summary>
public interface ISeriesUserStateService
{
    /// <summary>Computes the user's state for one library series.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="seriesJellyfinId">The series' Jellyfin item id.</param>
    /// <returns>The state, or <c>null</c> when the user/series can't be resolved or has no episodes.</returns>
    SeriesStateResult? GetState(Guid userId, Guid seriesJellyfinId);

    /// <summary>
    /// Returns the series the user has started (at least one played episode), most-recent activity
    /// first — the candidate set for continuity rows.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="max">Maximum series to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The started series (empty when none / no user).</returns>
    IReadOnlyList<StartedSeries> GetStartedSeries(Guid userId, int max, CancellationToken ct = default);

    /// <summary>
    /// Returns the Jellyfin ids of series that currently have an in-progress (resumable) episode —
    /// i.e. those already represented in "Continue Watching". Used to keep one series to one home
    /// card by keeping actively-resuming shows out of continuity rows.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resuming series ids (empty when none / no user).</returns>
    IReadOnlySet<Guid> GetResumingSeriesIds(Guid userId, CancellationToken ct = default);
}

/// <summary>A series the user has begun watching.</summary>
/// <param name="SeriesId">The series' Jellyfin item id.</param>
/// <param name="LastActivityUtc">The most recent play time across the series (UTC).</param>
public sealed record StartedSeries(Guid SeriesId, DateTime LastActivityUtc);
