using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Integrations.Arr;

/// <summary>A normalized calendar entry merged from Sonarr (episodes) and Radarr (movie releases).</summary>
public class ArrCalendarEntry
{
    /// <summary>Gets or sets the media type (Series for Sonarr episodes, Movie for Radarr).</summary>
    public MediaType MediaType { get; set; }

    /// <summary>Gets or sets the TMDB id (the cross-source join key), when known.</summary>
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the TVDB id (Sonarr series), when known.</summary>
    public int? TvdbId { get; set; }

    /// <summary>Gets or sets the IMDb id, when known.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the series or movie title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode title (Series only).</summary>
    public string? EpisodeTitle { get; set; }

    /// <summary>Gets or sets the season number (Series only).</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number (Series only).</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the air/release date (UTC).</summary>
    public DateTime AirDateUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the file is already downloaded.</summary>
    public bool HasFile { get; set; }
}

/// <summary>Wire model for a Sonarr v3 calendar item.</summary>
public class SonarrCalendarItem
{
    /// <summary>Gets or sets the episode title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    [JsonPropertyName("episodeNumber")]
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the UTC air date.</summary>
    [JsonPropertyName("airDateUtc")]
    public string? AirDateUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the episode is downloaded.</summary>
    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }

    /// <summary>Gets or sets the parent series (present when includeSeries=true).</summary>
    [JsonPropertyName("series")]
    public SonarrSeries? Series { get; set; }
}

/// <summary>Wire model for a Sonarr series (only the fields the engine reads).</summary>
public class SonarrSeries
{
    /// <summary>Gets or sets the series title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the TVDB id.</summary>
    [JsonPropertyName("tvdbId")]
    public int? TvdbId { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the TMDB id (Sonarr v3+).</summary>
    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; set; }
}

/// <summary>Wire model for a Radarr v3 calendar item (a movie).</summary>
public class RadarrCalendarItem
{
    /// <summary>Gets or sets the movie title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets a value indicating whether the movie is downloaded.</summary>
    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }

    /// <summary>Gets or sets the cinema release date.</summary>
    [JsonPropertyName("inCinemas")]
    public string? InCinemas { get; set; }

    /// <summary>Gets or sets the physical release date.</summary>
    [JsonPropertyName("physicalRelease")]
    public string? PhysicalRelease { get; set; }

    /// <summary>Gets or sets the digital release date.</summary>
    [JsonPropertyName("digitalRelease")]
    public string? DigitalRelease { get; set; }
}
