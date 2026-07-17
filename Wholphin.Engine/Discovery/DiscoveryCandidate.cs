using System.Collections.Generic;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// One external title proposed by a discovery source, before filtering/scoring. Candidates from
/// multiple sources are aggregated by (TMDB id, media type) with their attributions unioned, so
/// per-source provenance survives all the way into the persisted pick.
/// </summary>
public class DiscoveryCandidate
{
    /// <summary>Gets or sets the normalized TMDB payload.</summary>
    public DiscoverResult Result { get; set; } = new();

    /// <summary>Gets or sets the proposed justification kind (the aggregation keeps the strongest — seeded beats generic).</summary>
    public DiscoveryPickKind Kind { get; set; }

    /// <summary>Gets or sets which sources produced this candidate and how confident each is.</summary>
    public List<SourceAttribution> Attributions { get; set; } = new();

    /// <summary>Gets or sets the taste seed that produced this candidate, for seeded (Because You Watched) candidates.</summary>
    public TasteSeed? Seed { get; set; }

    /// <summary>
    /// Gets or sets the LLM's own one-line justification for proposing this title. When present it
    /// replaces the deterministic template reason on the persisted pick.
    /// </summary>
    public string? LlmRationale { get; set; }
}

/// <summary>Per-source provenance for a candidate: who proposed it and with what confidence.</summary>
public class SourceAttribution
{
    /// <summary>Gets or sets the source name (e.g. "seedrelated", "tastediscover", "trending", "country").</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Gets or sets the source's intrinsic confidence in its proposals (0-1).</summary>
    public double SourceConfidence { get; set; }

    /// <summary>Gets or sets an optional source-internal score (e.g. TMDB rank position), for analysis.</summary>
    public double? SourceScore { get; set; }
}
