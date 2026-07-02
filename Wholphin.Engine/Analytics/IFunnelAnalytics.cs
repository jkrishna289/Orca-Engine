using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Analytics;

/// <summary>Funnel counts + derived rates for a single catalog item.</summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="Title">The item title (best-effort).</param>
/// <param name="Shown">Impression count.</param>
/// <param name="Focused">Focus count.</param>
/// <param name="Clicked">Click count.</param>
/// <param name="Played">Playback-started count.</param>
/// <param name="Completed">Playback-completed count.</param>
/// <param name="FocusRate">Focused / Shown.</param>
/// <param name="Ctr">Clicked / Shown.</param>
/// <param name="PlayRate">Played / Clicked.</param>
/// <param name="CompletionRate">Completed / Played.</param>
public record ItemFunnel(
    Guid ItemId,
    string? Title,
    int Shown,
    int Focused,
    int Clicked,
    int Played,
    int Completed,
    double FocusRate,
    double Ctr,
    double PlayRate,
    double CompletionRate);

/// <summary>Aggregate funnel across all items.</summary>
/// <param name="Shown">Total impressions.</param>
/// <param name="Focused">Total focuses.</param>
/// <param name="Clicked">Total clicks.</param>
/// <param name="Played">Total plays.</param>
/// <param name="Completed">Total completions.</param>
/// <param name="FocusRate">Focused / Shown.</param>
/// <param name="Ctr">Clicked / Shown.</param>
/// <param name="PlayRate">Played / Clicked.</param>
/// <param name="CompletionRate">Completed / Played.</param>
public record FunnelSummary(
    int Shown,
    int Focused,
    int Clicked,
    int Played,
    int Completed,
    double FocusRate,
    double Ctr,
    double PlayRate,
    double CompletionRate);

/// <summary>The funnel report: overall summary plus the busiest items.</summary>
/// <param name="Summary">The aggregate funnel.</param>
/// <param name="Items">Per-item funnels, busiest first.</param>
public record FunnelReport(FunnelSummary Summary, IReadOnlyList<ItemFunnel> Items);

/// <summary>
/// Impression funnel analytics (Feature B): turns the behavior log's shown → focused → clicked →
/// played → completed signals into per-item and aggregate CTR / focus-rate / completion-rate.
/// Computed on demand and cached (the behavior log is the source of truth; a precomputed table can
/// sit behind this port later via the schema-migration runner if scale demands it).
/// </summary>
public interface IFunnelAnalytics
{
    /// <summary>Builds the funnel report (overall summary + top items by impressions).</summary>
    /// <param name="limit">Maximum items to return (1-200).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The funnel report.</returns>
    Task<FunnelReport> BuildAsync(int limit, CancellationToken ct = default);
}
