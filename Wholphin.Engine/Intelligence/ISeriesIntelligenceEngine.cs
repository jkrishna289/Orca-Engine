using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Integrations.Arr;

namespace Wholphin.Engine.Intelligence;

/// <summary>
/// The composition root for series intelligence: it unifies user watch-state (Jellyfin), intent
/// (affinity), and release events (*arr) behind one seam so the home rows never wire those sources
/// up themselves. Release state and user state stay strictly separated — this facade just serves both.
/// (The assembled home feed itself remains <see cref="Home.HomeService"/>'s job.)
/// </summary>
public interface ISeriesIntelligenceEngine
{
    /// <summary>Computes the user's state for one library series.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="seriesJellyfinId">The series' Jellyfin item id.</param>
    /// <returns>The state, or <c>null</c> when unresolved / no episodes.</returns>
    SeriesStateResult? GetSeriesState(Guid userId, Guid seriesJellyfinId);

    /// <summary>Returns the series the user has started, most-recent activity first.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="max">Maximum series to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The started series (empty when none / no user).</returns>
    IReadOnlyList<StartedSeries> GetStartedSeries(Guid userId, int max, CancellationToken ct = default);

    /// <summary>Returns series ids with an in-progress episode (already in "Continue Watching").</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resuming series ids (empty when none / no user).</returns>
    IReadOnlySet<Guid> GetResumingSeriesIds(Guid userId, CancellationToken ct = default);

    /// <summary>Builds an intent scorer bound to the user's current affinity snapshot.</summary>
    /// <param name="userId">The user to score for (null = anonymous, empty affinity).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The intent scorer.</returns>
    Task<IntentScorer> GetIntentScorerAsync(Guid? userId, CancellationToken ct = default);

    /// <summary>Returns *arr "download completed" (availability) events since the given instant.</summary>
    /// <param name="sinceUtc">Only events at/after this instant (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The release events (empty when unconfigured or on failure).</returns>
    Task<IReadOnlyList<ArrHistoryEvent>> GetReleaseEventsAsync(DateTime sinceUtc, CancellationToken ct = default);
}
