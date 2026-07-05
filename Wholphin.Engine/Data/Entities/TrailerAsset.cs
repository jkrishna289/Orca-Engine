using System;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// The durable state-machine record for one title's trailer, keyed by (<see cref="TmdbId"/>,
/// <see cref="MediaType"/>). One row per title; upserted as the trailer moves through its
/// <see cref="TrailerState"/> lifecycle. This is what makes trailer status queryable instead of
/// inferred from HTTP 404s.
/// </summary>
public class TrailerAsset
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the TMDB id (half of the natural key).</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the media type (half of the natural key).</summary>
    public MediaType MediaType { get; set; }

    /// <summary>Gets or sets the current lifecycle state.</summary>
    public TrailerState State { get; set; } = TrailerState.Unknown;

    /// <summary>Gets or sets the cached transcoded file path (set when <see cref="State"/> is Ready).</summary>
    public string? FilePath { get; set; }

    /// <summary>Gets or sets the cached file size in bytes (set when Ready).</summary>
    public long? FileBytes { get; set; }

    /// <summary>Gets or sets a short human-readable reason for the most recent failure.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets or sets how many times this trailer has failed (drives temporary-vs-permanent decisions + backoff).</summary>
    public int FailureCount { get; set; }

    /// <summary>Gets or sets when the trailer last became Ready.</summary>
    public DateTime? ReadyAt { get; set; }

    /// <summary>Gets or sets when this row was first created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when this row was last updated (any state transition).</summary>
    public DateTime UpdatedAt { get; set; }

    // ── Cache management + metadata (schema v10) ──────────────────────────────
    // All nullable so they can be added to an existing table without breaking materialization
    // (the codebase's additive-column convention); code coalesces null to sensible defaults.

    /// <summary>Gets or sets how many times the cached file has been served (LFU input for eviction).</summary>
    public int? AccessCount { get; set; }

    /// <summary>Gets or sets when the cached file was last served (recency input for eviction; throttled).</summary>
    public DateTime? LastAccessedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether this trailer is pinned (never evicted) — e.g. Continue Watching / frequently viewed.</summary>
    public bool? Pinned { get; set; }

    /// <summary>Gets or sets the transcoded trailer duration in milliseconds (metadata; null until probed).</summary>
    public int? DurationMs { get; set; }

    /// <summary>Gets or sets the recommended preview start offset in ms (smart skip; null → the client's default offset).</summary>
    public int? PreviewStartMs { get; set; }
}
