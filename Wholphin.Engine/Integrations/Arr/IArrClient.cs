using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Integrations.Arr;

/// <summary>
/// Fail-soft port over Sonarr + Radarr calendars — the primary air-date source for "New Since You
/// Were Away" (recent window) and "Coming Soon" (future window). Degrades to an empty list when
/// neither service is configured or a call fails; TMDB is the fallback at the provider layer.
/// </summary>
public interface IArrClient
{
    /// <summary>Gets a value indicating whether Sonarr or Radarr is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns merged Sonarr (episode) + Radarr (movie) calendar entries whose air/release date falls
    /// in [<paramref name="startUtc"/>, <paramref name="endUtc"/>].
    /// </summary>
    /// <param name="startUtc">Window start (UTC).</param>
    /// <param name="endUtc">Window end (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The calendar entries (empty when unconfigured or on failure).</returns>
    Task<IReadOnlyList<ArrCalendarEntry>> GetCalendarAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default);
}
