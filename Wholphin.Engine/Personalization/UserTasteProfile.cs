using System;
using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// The per-user taste embedding file, persisted as one JSON document per user under
/// <c>{DataPath}/wholphin-engine/profiles/{userId:N}.json</c>. The <see cref="Seeds"/> (the user's
/// strongest positively-weighted titles) are the durable representation — the stored vector is a
/// cache derived from them, recomputable in any embedding space. Drives taste-matched TMDB pulls
/// and the taste blend on trending.
/// </summary>
public class UserTasteProfile
{
    /// <summary>Gets or sets the Jellyfin user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the embedding provider that produced the stored vector (e.g. "ollama").</summary>
    public string Provider { get; set; } = "ollama";

    /// <summary>Gets or sets the taste vector: the weighted mean of the seeds' content vectors.</summary>
    public float[]? DenseVector { get; set; }

    /// <summary>Gets or sets the seed items the vector derives from, strongest first.</summary>
    public List<TasteSeed> Seeds { get; set; } = new();

    /// <summary>Gets or sets the user's strongest positive genres (affinity &gt; 0.35, max 5).</summary>
    public List<string> TopGenres { get; set; } = new();

    /// <summary>Gets or sets the user's strongest positive tags/keywords (affinity &gt; 0.35, max 8).</summary>
    public List<string> TopKeywords { get; set; } = new();

    /// <summary>
    /// Gets or sets the user's strongest original languages as ISO-639-1 codes (affinity &gt; 0.35,
    /// max 2), strongest first. Drives the language leg of the taste pull, so a viewer who watches
    /// mostly non-English cinema is offered candidates from it rather than whatever is globally
    /// popular in the same genre.
    /// </summary>
    public List<string> TopLanguages { get; set; } = new();

    /// <summary>Gets or sets genres the user actively avoids (affinity &lt; -0.5) — a hard eligibility veto.</summary>
    public List<string> AvoidGenres { get; set; } = new();

    /// <summary>Gets or sets the profile confidence (0-1; below ~0.40 taste pulls are skipped).</summary>
    public double Confidence { get; set; }

    /// <summary>Gets or sets the number of behavior events behind the profile.</summary>
    public int EventCount { get; set; }

    /// <summary>Gets or sets when the profile was built (UTC).</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>
    /// Picks the seeds to drive "Because You Watched" pulls: TMDB-bearing seeds ranked by weight
    /// scaled with recency (a strong old favorite should not crowd out what the user watches now).
    /// </summary>
    /// <param name="count">How many seeds to return.</param>
    /// <returns>The pull seeds, strongest-and-freshest first.</returns>
    public IReadOnlyList<TasteSeed> PullSeeds(int count)
    {
        var now = DateTime.UtcNow;
        return Seeds
            .Where(s => s.TmdbId is > 0)
            .OrderByDescending(s => s.Weight * Math.Pow(0.5, Math.Max(0, (now - s.LastEventAt).TotalDays) / 90.0))
            .Take(count)
            .ToList();
    }
}

/// <summary>
/// One taste-profile seed: a title the user engaged positively with, and how strongly. Seed
/// weights reuse the personalization signal weights with the same 90-day half-life decay, so the
/// taste file never disagrees with the affinity vector about what the user likes.
/// </summary>
public class TasteSeed
{
    /// <summary>Gets or sets the catalog item id.</summary>
    public long CatalogItemId { get; set; }

    /// <summary>Gets or sets the TMDB id, when known (required for seeded TMDB pulls).</summary>
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the media type.</summary>
    public MediaType MediaType { get; set; }

    /// <summary>Gets or sets the title, for display in "Because You Watched {Title}" rows.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the net positive signal weight (decayed sum over the user's events).</summary>
    public double Weight { get; set; }

    /// <summary>Gets or sets when the user last produced a signal on this title (UTC).</summary>
    public DateTime LastEventAt { get; set; }
}
