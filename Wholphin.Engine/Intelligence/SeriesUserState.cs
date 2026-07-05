using System;
using System.Collections.Generic;
using System.Linq;

namespace Wholphin.Engine.Intelligence;

/// <summary>
/// A user's relationship to a series, derived from their per-episode watch history — the "user
/// state" half of the intelligence model (kept strictly separate from release/availability state).
/// This is NOT a temporal event: an unwatched season is a <em>state</em>, never "new since away".
/// </summary>
public enum SeriesUserState
{
    /// <summary>The user has watched no episodes of this series.</summary>
    NotTracked,

    /// <summary>The user is progressing through the series with recent activity (normal in-progress).</summary>
    Watching,

    /// <summary>The user started the series but stopped long ago without finishing.</summary>
    Abandoned,

    /// <summary>The user has watched every available episode.</summary>
    Completed,

    /// <summary>The user watched a later season while an earlier season sits entirely unwatched (a story gap).</summary>
    SkippedBacklog,
}

/// <summary>Per-season watched tally for one series (specials/season 0 excluded by the calculator).</summary>
/// <param name="SeasonNumber">The season number (1-based).</param>
/// <param name="Episodes">Total available (non-virtual) episodes in the season.</param>
/// <param name="Watched">How many of those the user has played.</param>
public sealed record SeasonProgress(int SeasonNumber, int Episodes, int Watched);

/// <summary>The computed user-state result for one series.</summary>
/// <param name="State">The derived <see cref="SeriesUserState"/>.</param>
/// <param name="WatchedEpisodes">Total watched episodes across real seasons.</param>
/// <param name="TotalEpisodes">Total available episodes across real seasons.</param>
/// <param name="LastActivityUtc">The most recent episode play time, if any.</param>
/// <param name="GapSeasonNumber">For <see cref="SeriesUserState.SkippedBacklog"/>, the earliest skipped season.</param>
public sealed record SeriesStateResult(
    SeriesUserState State,
    int WatchedEpisodes,
    int TotalEpisodes,
    DateTime? LastActivityUtc,
    int? GapSeasonNumber);

/// <summary>
/// Pure derivation of <see cref="SeriesUserState"/> from season tallies + last-activity. No Jellyfin
/// (or any I/O) dependency, so the classification rules are unit-testable in isolation.
/// </summary>
public static class SeriesStateCalculator
{
    /// <summary>In-progress series untouched for longer than this are classified <see cref="SeriesUserState.Abandoned"/>.</summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// Derives the user state from per-season tallies. Only real seasons (number ≥ 1 with at least
    /// one available episode) count, so specials never distort completion or gap detection.
    /// </summary>
    /// <param name="seasons">Per-season progress (any order; season 0 ignored).</param>
    /// <param name="lastActivityUtc">The most recent play time across the series, if any.</param>
    /// <param name="nowUtc">The reference "now" (UTC) for the abandonment window.</param>
    /// <returns>The classified state.</returns>
    public static SeriesStateResult Derive(
        IReadOnlyList<SeasonProgress> seasons,
        DateTime? lastActivityUtc,
        DateTime nowUtc)
    {
        var real = seasons
            .Where(s => s.SeasonNumber >= 1 && s.Episodes > 0)
            .OrderBy(s => s.SeasonNumber)
            .ToList();

        var total = real.Sum(s => s.Episodes);
        var watched = real.Sum(s => Math.Min(s.Watched, s.Episodes));

        if (total == 0 || watched == 0)
        {
            return new SeriesStateResult(SeriesUserState.NotTracked, 0, total, lastActivityUtc, null);
        }

        if (watched >= total)
        {
            return new SeriesStateResult(SeriesUserState.Completed, watched, total, lastActivityUtc, null);
        }

        // A story gap: a fully-unwatched season sitting BELOW a season the user has already touched.
        // (Unwatched seasons ABOVE the highest touched one are just "not yet reached" — not a gap.)
        var highestTouched = real.Where(s => s.Watched > 0).Max(s => s.SeasonNumber);
        var gap = real.FirstOrDefault(s => s.Watched == 0 && s.SeasonNumber < highestTouched);
        if (gap is not null)
        {
            return new SeriesStateResult(SeriesUserState.SkippedBacklog, watched, total, lastActivityUtc, gap.SeasonNumber);
        }

        var recent = lastActivityUtc is { } last && (nowUtc - last) <= AbandonAfter;
        var state = recent ? SeriesUserState.Watching : SeriesUserState.Abandoned;
        return new SeriesStateResult(state, watched, total, lastActivityUtc, null);
    }
}
