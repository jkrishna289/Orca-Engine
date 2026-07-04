using System;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// A unified catalog entry — an available (Jellyfin) or not-yet-available (Jellyseerr/TMDB) title.
/// Joined across sources by TMDB id (fallbacks: IMDb/TVDB id, then title+year).
/// </summary>
public class CatalogItem
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the Jellyfin item id (present when available in the library).</summary>
    public Guid? JellyfinItemId { get; set; }

    /// <summary>Gets or sets the TMDB id (primary cross-source join key).</summary>
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the TVDB id.</summary>
    public int? TvdbId { get; set; }

    /// <summary>Gets or sets the media type.</summary>
    public MediaType MediaType { get; set; }

    /// <summary>Gets or sets the availability state.</summary>
    public AvailabilityState Availability { get; set; } = AvailabilityState.Unavailable;

    /// <summary>Gets or sets the display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the original-language title.</summary>
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the ISO-639-1 original language (e.g. "en", "hi"). Feeds the Language affinity dimension.</summary>
    public string? OriginalLanguage { get; set; }

    /// <summary>Gets or sets the collection/franchise name (e.g. "The Dark Knight Collection"). Feeds the Franchise affinity dimension + diversity.</summary>
    public string? CollectionName { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the premiere date.</summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>Gets or sets the runtime in minutes.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Gets or sets the official (age) rating.</summary>
    public string? OfficialRating { get; set; }

    /// <summary>Gets or sets the community rating.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets the critic rating.</summary>
    public float? CriticRating { get; set; }

    /// <summary>Gets or sets the aggregated Wholphin community rating (0-10), computed locally from behavior signals.</summary>
    public double? WholphinRating { get; set; }

    /// <summary>Gets or sets the number of local signals behind the Wholphin rating.</summary>
    public int? WholphinVotes { get; set; }

    /// <summary>Gets or sets the overview/synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets an absolute poster (2:3) image URL. Populated for requestable/discover items
    /// (which have no Jellyfin id for the client to resolve art from) — typically a TMDB image URL.
    /// Null for library items, whose art the client builds from <see cref="JellyfinItemId"/>.
    /// </summary>
    public string? PosterImageUrl { get; set; }

    /// <summary>Gets or sets an absolute backdrop (16:9) image URL (TMDB), for hero/billboard cards.</summary>
    public string? BackdropImageUrl { get; set; }

    /// <summary>Gets or sets a trailer URL (typically a YouTube watch URL from TMDB). Informational; not a directly-playable stream.</summary>
    public string? TrailerUrl { get; set; }

    /// <summary>Gets or sets the genres as a JSON array (denormalized for fast reads).</summary>
    public string? GenresJson { get; set; }

    /// <summary>Gets or sets the studios as a JSON array.</summary>
    public string? StudiosJson { get; set; }

    /// <summary>Gets or sets the TMDB id of the primary streaming provider (studio/provider tag + logo cache key).</summary>
    public int? ProviderBrandId { get; set; }

    /// <summary>Gets or sets the primary streaming-provider brand name (e.g., "Netflix", "Prime Video").</summary>
    public string? ProviderBrandName { get; set; }

    /// <summary>Gets or sets when the watch-provider brand was last resolved (null = never; drives round-robin enrichment).</summary>
    public DateTime? ProvidersSyncedAt { get; set; }

    /// <summary>Gets or sets the LLM-generated content advisories for this title (movie/series-level), as serialized JSON. Null = not yet computed.</summary>
    public string? ContentWarningsJson { get; set; }

    /// <summary>Gets or sets when content warnings were last attempted (null = never; drives round-robin pre-generation).</summary>
    public DateTime? ContentWarningsSyncedAt { get; set; }

    /// <summary>Gets or sets the people (cast/crew) as a JSON array.</summary>
    public string? PeopleJson { get; set; }

    /// <summary>Gets or sets the tags as a JSON array.</summary>
    public string? TagsJson { get; set; }

    /// <summary>Gets or sets the derived feature vector (built by the metadata/feature pipeline).</summary>
    public string? FeatureVectorJson { get; set; }

    /// <summary>Gets or sets when the item was added to the library.</summary>
    public DateTime? DateAdded { get; set; }

    /// <summary>Gets or sets when this row was last synced.</summary>
    public DateTime LastSyncedAt { get; set; }
}
