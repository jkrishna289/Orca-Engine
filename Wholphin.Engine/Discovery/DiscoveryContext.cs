using System;
using System.Collections.Generic;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Personalization;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// The read-only context one pipeline run operates on, loaded once by the orchestrator so the
/// stages themselves never touch the database (strict stage contract: only the persistence stage
/// writes; only the orchestrator loads).
/// </summary>
public class DiscoveryContext
{
    /// <summary>Gets or sets the user the run pulls for (<see cref="Guid.Empty"/> = a global trending/country run).</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's taste profile (null on global runs).</summary>
    public UserTasteProfile? Profile { get; set; }

    /// <summary>Gets or sets the user's affinity vector (null on global runs; feeds novelty + genre bonus).</summary>
    public AffinityVector? Affinity { get; set; }

    /// <summary>Gets or sets the catalog rows backing the profile seeds (for same-batch embedding).</summary>
    public IReadOnlyList<CatalogItem> SeedItems { get; set; } = Array.Empty<CatalogItem>();

    /// <summary>Gets or sets the effective settings for the run's scope.</summary>
    public EngineSettings Settings { get; set; } = new();

    /// <summary>Gets or sets the ISO-3166-1 country for country runs (null otherwise).</summary>
    public string? CountryCode { get; set; }

    /// <summary>Gets or sets the TMDB ids already in the library (hard-ineligible for discovery).</summary>
    public IReadOnlySet<int> LibraryTmdbIds { get; set; } = new HashSet<int>();

    /// <summary>Gets or sets the blacklisted TMDB ids (catalog Availability = Unavailable).</summary>
    public IReadOnlySet<int> BlacklistedTmdbIds { get; set; } = new HashSet<int>();

    /// <summary>Gets or sets the user's recommendation memory, keyed by (TMDB id, media type).</summary>
    public IReadOnlyDictionary<(int TmdbId, MediaType MediaType), UserItemMemory> Memory { get; set; }
        = new Dictionary<(int, MediaType), UserItemMemory>();

    /// <summary>Gets or sets the TMDB genre name→id inversions per media type (for filtered discover calls).</summary>
    public IReadOnlyDictionary<MediaType, IReadOnlyDictionary<string, int>> GenreIdsByName { get; set; }
        = new Dictionary<MediaType, IReadOnlyDictionary<string, int>>();

    /// <summary>
    /// Gets or sets titles the user explicitly rejected (thumbs down / rating ≤ 3), newest first —
    /// the LLM source's negative constraints. Loaded only when LLM discovery is active.
    /// </summary>
    public IReadOnlyList<Llm.HistoryLine> DislikedTitles { get; set; } = Array.Empty<Llm.HistoryLine>();

    /// <summary>Gets or sets the resolved admin tuning knobs for this run.</summary>
    public DiscoveryTuning Tuning { get; set; } = new();

    /// <summary>Gets or sets when the run started (UTC); one clock for every stage.</summary>
    public DateTime Now { get; set; } = DateTime.UtcNow;
}
