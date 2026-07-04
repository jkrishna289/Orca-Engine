using System;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// A per-pull funnel report for the discovery pipeline: how many candidates each stage saw and
/// dropped (with reasons), so recommendation quality is observable and tunable. One row per
/// pipeline run (per user per cycle, plus global runs). Added in schema v7; swept after 30 days.
/// </summary>
public class DiscoveryRun
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the user the run pulled for (<see cref="Guid.Empty"/> = global run).</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets when the run started (UTC).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets the run duration in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets how many candidates the sources generated (before aggregation).</summary>
    public int Generated { get; set; }

    /// <summary>Gets or sets how many candidates the eligibility filter dropped.</summary>
    public int FilteredOut { get; set; }

    /// <summary>Gets or sets a JSON object of drop counts keyed by filter reason (e.g. {"in_library":12}).</summary>
    public string? FilterReasonsJson { get; set; }

    /// <summary>Gets or sets how many candidates were scored.</summary>
    public int Scored { get; set; }

    /// <summary>Gets or sets how many candidates the diversity stage re-ordered (moved from their rank position).</summary>
    public int DiversityReordered { get; set; }

    /// <summary>Gets or sets how many ranked candidates fell below the selection thresholds.</summary>
    public int BelowThreshold { get; set; }

    /// <summary>Gets or sets how many candidates were selected as picks.</summary>
    public int Selected { get; set; }

    /// <summary>Gets or sets how many new catalog rows the run imported.</summary>
    public int Imported { get; set; }

    /// <summary>Gets or sets a JSON object of per-source generated/selected counts.</summary>
    public string? PerSourceJson { get; set; }
}
