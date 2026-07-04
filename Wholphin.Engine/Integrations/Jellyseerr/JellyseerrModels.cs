using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Integrations.Jellyseerr;

/// <summary>
/// Jellyseerr/Overseerr media status codes (the <c>mediaInfo.status</c> field) and their
/// mapping into the engine's <see cref="AvailabilityState"/>.
/// </summary>
public static class JellyseerrStatus
{
    /// <summary>Status is not tracked by Jellyseerr (treat as requestable).</summary>
    public const int Unknown = 1;

    /// <summary>A request is pending approval.</summary>
    public const int Pending = 2;

    /// <summary>The download is being processed.</summary>
    public const int Processing = 3;

    /// <summary>Some seasons/parts are available.</summary>
    public const int PartiallyAvailable = 4;

    /// <summary>Fully available in the library.</summary>
    public const int Available = 5;

    /// <summary>Blacklisted by an admin (must not be requested).</summary>
    public const int Blacklisted = 6;

    /// <summary>Previously tracked but deleted from the library (requestable again).</summary>
    public const int Deleted = 7;

    /// <summary>Maps a Jellyseerr status code (or null) to an engine availability state.</summary>
    /// <param name="status">The raw <c>mediaInfo.status</c>, or null when absent.</param>
    /// <returns>The mapped availability state.</returns>
    public static AvailabilityState ToAvailability(int? status) => status switch
    {
        Available or PartiallyAvailable => AvailabilityState.WatchNow,
        Processing => AvailabilityState.Downloading,
        Pending => AvailabilityState.Requested,
        Blacklisted => AvailabilityState.Unavailable,
        // Unknown (1), Deleted (7), null, or any future code → requestable.
        _ => AvailabilityState.Request,
    };
}

/// <summary>Wire model for a Jellyseerr media-info block.</summary>
public class JellyseerrMediaInfo
{
    /// <summary>Gets or sets the media status code (see <see cref="JellyseerrStatus"/>).</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }
}

/// <summary>Wire model for a Jellyseerr movie/tv detail response (only the fields the engine reads).</summary>
public class JellyseerrDetail
{
    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the movie title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the movie release date (yyyy-MM-dd).</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the series first-air date (yyyy-MM-dd).</summary>
    [JsonPropertyName("firstAirDate")]
    public string? FirstAirDate { get; set; }

    /// <summary>Gets or sets the TMDB vote average.</summary>
    [JsonPropertyName("voteAverage")]
    public double? VoteAverage { get; set; }

    /// <summary>Gets or sets the media-info block (present when Jellyseerr tracks the title).</summary>
    [JsonPropertyName("mediaInfo")]
    public JellyseerrMediaInfo? MediaInfo { get; set; }
}

/// <summary>Wire model for a single Jellyseerr discover result.</summary>
public class JellyseerrDiscoverItem
{
    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the discriminator ("movie" or "tv") on combined discover endpoints.</summary>
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the movie title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the movie release date.</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the series first-air date.</summary>
    [JsonPropertyName("firstAirDate")]
    public string? FirstAirDate { get; set; }

    /// <summary>Gets or sets the TMDB vote average.</summary>
    [JsonPropertyName("voteAverage")]
    public double? VoteAverage { get; set; }

    /// <summary>Gets or sets the media-info block (present when Jellyseerr tracks the title).</summary>
    [JsonPropertyName("mediaInfo")]
    public JellyseerrMediaInfo? MediaInfo { get; set; }
}

/// <summary>Wire model for a Jellyseerr discover page.</summary>
public class JellyseerrDiscoverPage
{
    /// <summary>Gets or sets the current page number.</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>Gets or sets the total page count.</summary>
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    /// <summary>Gets or sets the page results.</summary>
    [JsonPropertyName("results")]
    public List<JellyseerrDiscoverItem> Results { get; set; } = new();
}

/// <summary>Wire model for a Jellyseerr user (only the fields needed to map from a Jellyfin user).</summary>
public class JellyseerrUser
{
    /// <summary>Gets or sets the Jellyseerr user id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the linked Jellyfin user id (dash-less GUID), when this user came from Jellyfin.</summary>
    [JsonPropertyName("jellyfinUserId")]
    public string? JellyfinUserId { get; set; }
}

/// <summary>Wire model for a Jellyseerr user list page.</summary>
public class JellyseerrUserPage
{
    /// <summary>Gets or sets the users on this page.</summary>
    [JsonPropertyName("results")]
    public List<JellyseerrUser> Results { get; set; } = new();
}

/// <summary>A normalized, source-agnostic discovery result the engine can map into the catalog.</summary>
public class DiscoverResult
{
    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the media type.</summary>
    public MediaType MediaType { get; set; }

    /// <summary>Gets or sets the display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the community (TMDB) rating.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets the TMDB popularity score, when the source supplies it (TMDB direct; null from Jellyseerr).</summary>
    public double? Popularity { get; set; }

    /// <summary>Gets or sets the ISO-639-1 original language, when the source supplies it (TMDB direct; null from Jellyseerr).</summary>
    public string? OriginalLanguage { get; set; }

    /// <summary>Gets or sets the genre names, when the source supplies them (TMDB direct; Jellyseerr discover gives ids only).</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>Gets or sets an absolute poster (2:3) image URL, when the source supplies one.</summary>
    public string? PosterImageUrl { get; set; }

    /// <summary>Gets or sets an absolute backdrop (16:9) image URL, when the source supplies one.</summary>
    public string? BackdropImageUrl { get; set; }

    /// <summary>Gets or sets a trailer URL (e.g., a YouTube watch URL), when the source supplies one.</summary>
    public string? TrailerUrl { get; set; }

    /// <summary>Gets or sets the mapped availability state.</summary>
    public AvailabilityState Availability { get; set; } = AvailabilityState.Request;
}
