using System;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// A justification record for a currently-recommended external title: which user it was picked
/// for, why (kind + display reason), and the score components that ranked it. Home rows for
/// external content are built FROM these rows, so nothing external renders without one.
/// Added in schema v7.
/// </summary>
/// <remarks>
/// Global picks (Trending / Country rows, shared by all users) use <see cref="Guid.Empty"/> as
/// <see cref="UserId"/>. Per-user queries must therefore filter <c>UserId == user</c> exactly —
/// never with inequality checks that would sweep the globals in.
/// </remarks>
public class UserDiscoveryPick
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the Jellyfin user the pick belongs to (<see cref="Guid.Empty"/> = global).</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the catalog item the pick points at.</summary>
    public long CatalogItemId { get; set; }

    /// <summary>Gets or sets the justification category.</summary>
    public DiscoveryPickKind Kind { get; set; }

    /// <summary>Gets or sets the discovery source that produced the winning candidate (e.g. "seedrelated").</summary>
    public string? SourceType { get; set; }

    /// <summary>Gets or sets the human-readable justification shown on the card (e.g. "Because you watched Dark").</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the TMDB id of the seed title, for <see cref="DiscoveryPickKind.BecauseYouWatched"/> picks.</summary>
    public int? SeedTmdbId { get; set; }

    /// <summary>Gets or sets the seed title's name at pick time, for row headings.</summary>
    public string? SeedTitle { get; set; }

    /// <summary>Gets or sets the ISO-3166-1 country code, for <see cref="DiscoveryPickKind.Country"/> picks.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the final blended ranking score.</summary>
    public double FinalScore { get; set; }

    /// <summary>Gets or sets the taste-similarity component (embedding cosine + affinity overlap).</summary>
    public double TasteScore { get; set; }

    /// <summary>Gets or sets the normalized popularity component.</summary>
    public double PopularityScore { get; set; }

    /// <summary>Gets or sets the freshness (release recency) component.</summary>
    public double FreshnessScore { get; set; }

    /// <summary>
    /// Gets or sets a debug-only JSON blob with the remaining score detail (novelty, diversity
    /// penalty, per-source attributions). Core components live in real columns; this is never
    /// queried for ranking.
    /// </summary>
    public string? ScoreExplanationJson { get; set; }

    /// <summary>Gets or sets when the pick was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when the pick stops being shown (per-kind TTL; null = no expiry).</summary>
    public DateTime? ExpiresAt { get; set; }
}
