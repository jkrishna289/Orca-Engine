using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wholphin.Engine.Integrations.Tmdb;

/// <summary>The catalog list a TMDB-direct discovery pull draws from.</summary>
public enum TmdbDiscoverCategory
{
    /// <summary>Trending this week.</summary>
    Trending,

    /// <summary>Most popular.</summary>
    Popular,

    /// <summary>Highest rated.</summary>
    TopRated,
}

/// <summary>A TMDB genre {id, name}.</summary>
public class TmdbGenre
{
    /// <summary>Gets or sets the TMDB genre id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the genre name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>Response of the <c>/genre/{movie|tv}/list</c> endpoint.</summary>
public class TmdbGenreList
{
    /// <summary>Gets or sets the genres.</summary>
    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = new();
}

/// <summary>A TMDB video entry (trailer/teaser/etc.).</summary>
public class TmdbVideo
{
    /// <summary>Gets or sets the host site (e.g., "YouTube").</summary>
    [JsonPropertyName("site")]
    public string? Site { get; set; }

    /// <summary>Gets or sets the video type (e.g., "Trailer", "Teaser").</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Gets or sets the site-specific key (the YouTube video id).</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>Gets or sets a value indicating whether this is an official upload.</summary>
    [JsonPropertyName("official")]
    public bool Official { get; set; }
}

/// <summary>The <c>videos</c> append-to-response block.</summary>
public class TmdbVideos
{
    /// <summary>Gets or sets the video entries.</summary>
    [JsonPropertyName("results")]
    public List<TmdbVideo> Results { get; set; } = new();
}

/// <summary>
/// The <c>keywords</c> append-to-response block. TMDB is inconsistent: movie keywords live under
/// <c>keywords</c>, TV keywords under <c>results</c> — we read both and merge.
/// </summary>
public class TmdbKeywords
{
    /// <summary>Gets or sets the movie keyword list.</summary>
    [JsonPropertyName("keywords")]
    public List<TmdbGenre> Keywords { get; set; } = new();

    /// <summary>Gets or sets the TV keyword list.</summary>
    [JsonPropertyName("results")]
    public List<TmdbGenre> Results { get; set; } = new();
}

/// <summary>A TMDB credit entry (cast or crew).</summary>
public class TmdbPerson
{
    /// <summary>Gets or sets the person's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the crew job (e.g., "Director", "Screenplay"); null for cast.</summary>
    [JsonPropertyName("job")]
    public string? Job { get; set; }

    /// <summary>Gets or sets the crew department (e.g., "Writing", "Directing"); null for cast.</summary>
    [JsonPropertyName("department")]
    public string? Department { get; set; }
}

/// <summary>The <c>credits</c> append-to-response block.</summary>
public class TmdbCredits
{
    /// <summary>Gets or sets the cast.</summary>
    [JsonPropertyName("cast")]
    public List<TmdbPerson> Cast { get; set; } = new();

    /// <summary>Gets or sets the crew.</summary>
    [JsonPropertyName("crew")]
    public List<TmdbPerson> Crew { get; set; } = new();
}

/// <summary>A TMDB episode stub (used by <c>next_episode_to_air</c> / <c>last_episode_to_air</c>).</summary>
public class TmdbEpisodeStub
{
    /// <summary>Gets or sets the air date (yyyy-MM-dd).</summary>
    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    [JsonPropertyName("episode_number")]
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>A TMDB movie/tv detail (only the fields the engine reads), with appended videos.</summary>
public class TmdbDetail
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
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the series first-air date (yyyy-MM-dd).</summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    /// <summary>Gets or sets the TMDB vote average.</summary>
    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }

    /// <summary>Gets or sets the movie runtime in minutes.</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    /// <summary>Gets or sets the series per-episode runtimes (minutes).</summary>
    [JsonPropertyName("episode_run_time")]
    public List<int> EpisodeRunTime { get; set; } = new();

    /// <summary>Gets or sets the genres (names included on detail).</summary>
    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = new();

    /// <summary>Gets or sets the poster path (relative; prefix with the image base).</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the backdrop path (relative; prefix with the image base).</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    /// <summary>Gets or sets the appended videos block.</summary>
    [JsonPropertyName("videos")]
    public TmdbVideos? Videos { get; set; }

    /// <summary>Gets or sets the appended keywords block.</summary>
    [JsonPropertyName("keywords")]
    public TmdbKeywords? Keywords { get; set; }

    /// <summary>Gets or sets the appended credits block.</summary>
    [JsonPropertyName("credits")]
    public TmdbCredits? Credits { get; set; }

    /// <summary>Gets or sets the next episode to air (TV only).</summary>
    [JsonPropertyName("next_episode_to_air")]
    public TmdbEpisodeStub? NextEpisodeToAir { get; set; }

    /// <summary>Gets or sets the last episode to air (TV only).</summary>
    [JsonPropertyName("last_episode_to_air")]
    public TmdbEpisodeStub? LastEpisodeToAir { get; set; }
}

/// <summary>Which "related titles" TMDB list to pull.</summary>
public enum TmdbRelatedKind
{
    /// <summary>TMDB's personalized-ish recommendations for a title.</summary>
    Recommendations,

    /// <summary>TMDB's content-similar titles.</summary>
    Similar,
}

/// <summary>A normalized upcoming-episode result for the "Coming Soon" calendar.</summary>
public class TmdbUpcomingEpisode
{
    /// <summary>Gets or sets the series TMDB id.</summary>
    public int SeriesTmdbId { get; set; }

    /// <summary>Gets or sets the series title.</summary>
    public string? SeriesTitle { get; set; }

    /// <summary>Gets or sets the air date.</summary>
    public DateTime AirDate { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the episode name.</summary>
    public string? EpisodeName { get; set; }
}

/// <summary>A single TMDB discover/trending result.</summary>
public class TmdbDiscoverItem
{
    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the discriminator ("movie"/"tv") on combined endpoints.</summary>
    [JsonPropertyName("media_type")]
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
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the series first-air date.</summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    /// <summary>Gets or sets the TMDB vote average.</summary>
    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }

    /// <summary>Gets or sets the genre ids (discover gives ids only — resolve via the genre map).</summary>
    [JsonPropertyName("genre_ids")]
    public List<int> GenreIds { get; set; } = new();

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }
}

/// <summary>A page of TMDB discover/trending results.</summary>
public class TmdbDiscoverPage
{
    /// <summary>Gets or sets the current page.</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>Gets or sets the total page count.</summary>
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    /// <summary>Gets or sets the results.</summary>
    [JsonPropertyName("results")]
    public List<TmdbDiscoverItem> Results { get; set; } = new();
}

/// <summary>A single watch provider (streaming service) from the <c>/watch/providers</c> endpoint.</summary>
public class TmdbProvider
{
    /// <summary>Gets or sets the TMDB provider id (stable; used as the logo cache key).</summary>
    [JsonPropertyName("provider_id")]
    public int ProviderId { get; set; }

    /// <summary>Gets or sets the provider display name (e.g., "Netflix", "Amazon Prime Video").</summary>
    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; set; }

    /// <summary>Gets or sets the provider logo path (relative; prefix with the image base).</summary>
    [JsonPropertyName("logo_path")]
    public string? LogoPath { get; set; }

    /// <summary>Gets or sets the region-specific display priority (lower = more prominent).</summary>
    [JsonPropertyName("display_priority")]
    public int DisplayPriority { get; set; }
}

/// <summary>The provider lists for one region within a <c>/watch/providers</c> response.</summary>
public class TmdbWatchProviderRegion
{
    /// <summary>Gets or sets the flat-rate (subscription/streaming) providers — the "on Netflix/Prime" brands.</summary>
    [JsonPropertyName("flatrate")]
    public List<TmdbProvider> Flatrate { get; set; } = new();
}

/// <summary>Response of the <c>/{movie|tv}/{id}/watch/providers</c> endpoint (results keyed by ISO country).</summary>
public class TmdbWatchProvidersResponse
{
    /// <summary>Gets or sets the per-region provider lists (key = ISO-3166-1 country code, e.g., "US", "IN").</summary>
    [JsonPropertyName("results")]
    public Dictionary<string, TmdbWatchProviderRegion> Results { get; set; } = new();
}

/// <summary>The primary streaming-provider brand chosen for a title (for the studio/provider card tag).</summary>
public class TmdbProviderBrand
{
    /// <summary>Gets or sets the TMDB provider id (logo cache key).</summary>
    public int ProviderId { get; set; }

    /// <summary>Gets or sets the display name (e.g., "Netflix", "Prime Video").</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider logo path (relative; prefix with the image base to download).</summary>
    public string? LogoPath { get; set; }
}

/// <summary>
/// Normalized TMDB enrichment for a single title — genre names + artwork + trailer derived from a
/// TMDB detail lookup, ready to merge onto an existing catalog row.
/// </summary>
public class TmdbEnrichment
{
    /// <summary>Gets or sets the genre names.</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>Gets or sets the keyword/theme names (TMDB keywords → catalog tags).</summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>Gets or sets the role-prefixed people names ("Actor:X"/"Director:Y"/"Writer:Z").</summary>
    public List<string> People { get; set; } = new();

    /// <summary>Gets or sets the absolute poster URL.</summary>
    public string? PosterImageUrl { get; set; }

    /// <summary>Gets or sets the absolute backdrop URL.</summary>
    public string? BackdropImageUrl { get; set; }

    /// <summary>Gets or sets the trailer URL (YouTube watch URL).</summary>
    public string? TrailerUrl { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the community (TMDB) rating.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets the runtime in minutes.</summary>
    public int? RuntimeMinutes { get; set; }
}
