using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// The funnel accumulator for one pipeline run: how many candidates each stage saw and dropped.
/// Filled in by the orchestrator as the stages report, completed and persisted (as a
/// <see cref="Data.Entities.DiscoveryRun"/> row) by the persistence stage.
/// </summary>
public class DiscoveryRunReport
{
    /// <summary>Gets or sets the user the run pulled for (<see cref="Guid.Empty"/> = global run).</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets when the run started (UTC).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets how many candidates the sources generated (before aggregation).</summary>
    public int Generated { get; set; }

    /// <summary>Gets or sets how many candidates the eligibility filter dropped.</summary>
    public int FilteredOut { get; set; }

    /// <summary>Gets or sets the drop counts keyed by filter reason.</summary>
    public Dictionary<FilterReason, int> FilterReasons { get; set; } = new();

    /// <summary>Gets or sets how many candidates were scored.</summary>
    public int Scored { get; set; }

    /// <summary>Gets or sets how many candidates the diversity stage moved from their rank position.</summary>
    public int DiversityReordered { get; set; }

    /// <summary>Gets or sets how many ranked candidates fell below the selection thresholds/caps.</summary>
    public int BelowThreshold { get; set; }

    /// <summary>Gets or sets how many picks were selected.</summary>
    public int Selected { get; set; }

    /// <summary>Gets or sets per-source generated counts (selected counts are derived at persist time).</summary>
    public Dictionary<string, int> GeneratedBySource { get; set; } = new();
}
