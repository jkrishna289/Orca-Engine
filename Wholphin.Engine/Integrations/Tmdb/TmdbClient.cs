using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Integrations.Jellyseerr;

namespace Wholphin.Engine.Integrations.Tmdb;

/// <summary>
/// HTTP adapter for <see cref="ITmdbClient"/>. Reads the TMDB API key from the admin plugin
/// configuration and talks to the TMDB v3 REST API. Supports both a v3 key (sent as the
/// <c>api_key</c> query parameter) and a v4 read-access token (sent as a Bearer header — detected by
/// the dotted JWT shape). Every call is wrapped so a failure degrades to a neutral result.
/// </summary>
public class TmdbClient : ITmdbClient
{
    private const string ApiBase = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/";
    private const string PosterSize = "w500";
    private const string BackdropSize = "w1280";

    private static readonly TimeSpan GenreTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<TmdbClient> _logger;

    // Cached genre-id → name maps per media type (TMDB genre lists change rarely).
    private readonly ConcurrentDictionary<MediaType, (IReadOnlyDictionary<int, string> Map, DateTime At)> _genreCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory provided by Jellyfin.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public TmdbClient(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured
    {
        get
        {
            var config = Plugin.Instance?.Configuration;
            return config is not null && !string.IsNullOrWhiteSpace(config.TmdbApiKey);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> GetGenreMapAsync(MediaType mediaType, CancellationToken ct = default)
    {
        if (_genreCache.TryGetValue(mediaType, out var cached) && DateTime.UtcNow - cached.At < GenreTtl)
        {
            return cached.Map;
        }

        var empty = (IReadOnlyDictionary<int, string>)new Dictionary<int, string>();
        var kind = MediaKind(mediaType);
        if (kind is null || !TryConfig(out var apiKey))
        {
            return cached.Map ?? empty;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = Build(HttpMethod.Get, Url(apiKey, $"/genre/{kind}/list"), apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return cached.Map ?? empty;
            }

            var list = await response.Content.ReadFromJsonAsync<TmdbGenreList>(JsonOptions, ct).ConfigureAwait(false);
            var map = new Dictionary<int, string>();
            foreach (var g in list?.Genres ?? new List<TmdbGenre>())
            {
                if (!string.IsNullOrWhiteSpace(g.Name))
                {
                    map[g.Id] = g.Name!;
                }
            }

            var result = (IReadOnlyDictionary<int, string>)map;
            _genreCache[mediaType] = (result, DateTime.UtcNow);
            return result;
        }
        catch (Exception ex)
        {
            _metrics.Increment("tmdb.genre.error");
            _logger.LogWarning(ex, "Orca Engine: TMDB genre-list fetch failed for {Kind}.", kind);
            return cached.Map ?? empty;
        }
    }

    /// <inheritdoc />
    public async Task<TmdbEnrichment?> EnrichAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default)
    {
        var kind = MediaKind(mediaType);
        if (kind is null || tmdbId <= 0 || !TryConfig(out var apiKey))
        {
            return null;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            // One request fetches the detail plus videos/credits/keywords (cheaper than 3 round-trips).
            using var request = Build(HttpMethod.Get, Url(apiKey, $"/{kind}/{tmdbId}", "append_to_response=videos,credits,keywords"), apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("tmdb.enrich.error");
                return null;
            }

            var detail = await response.Content.ReadFromJsonAsync<TmdbDetail>(JsonOptions, ct).ConfigureAwait(false);
            if (detail is null)
            {
                return null;
            }

            // Movies carry a single runtime; series carry per-episode runtimes — take the first positive one.
            var runtime = detail.Runtime is > 0 ? detail.Runtime : null;
            if (runtime is null)
            {
                var ep = detail.EpisodeRunTime.FirstOrDefault(r => r > 0);
                runtime = ep > 0 ? ep : null;
            }

            _metrics.Increment("tmdb.enrich.ok");
            return new TmdbEnrichment
            {
                Genres = detail.Genres
                    .Select(g => g.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .ToList(),
                Keywords = ExtractKeywords(detail.Keywords),
                People = ExtractPeople(detail.Credits),
                PosterImageUrl = Image(PosterSize, detail.PosterPath),
                BackdropImageUrl = Image(BackdropSize, detail.BackdropPath),
                TrailerUrl = Trailer(detail.Videos),
                Overview = detail.Overview,
                Year = ParseYear(detail.ReleaseDate ?? detail.FirstAirDate),
                CommunityRating = detail.VoteAverage is { } v ? (float)v : null,
                RuntimeMinutes = runtime,
                OriginalLanguage = string.IsNullOrWhiteSpace(detail.OriginalLanguage) ? null : detail.OriginalLanguage,
                CollectionName = string.IsNullOrWhiteSpace(detail.BelongsToCollection?.Name) ? null : detail.BelongsToCollection!.Name,
            };
        }
        catch (Exception ex)
        {
            _metrics.Increment("tmdb.enrich.error");
            _logger.LogWarning(ex, "Orca Engine: TMDB enrich failed for {Kind} {TmdbId}.", kind, tmdbId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoverResult>> DiscoverAsync(MediaType mediaType, TmdbDiscoverCategory category, int page, CancellationToken ct = default)
    {
        var kind = MediaKind(mediaType);
        if (kind is null || !TryConfig(out var apiKey))
        {
            return Array.Empty<DiscoverResult>();
        }

        var path = category switch
        {
            TmdbDiscoverCategory.Trending => $"/trending/{kind}/week",
            TmdbDiscoverCategory.Popular => $"/{kind}/popular",
            TmdbDiscoverCategory.TopRated => $"/{kind}/top_rated",
            _ => $"/{kind}/popular",
        };
        var safePage = page < 1 ? 1 : page;

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = Build(HttpMethod.Get, Url(apiKey, path, $"page={safePage}"), apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("tmdb.discover.error");
                return Array.Empty<DiscoverResult>();
            }

            var pageResult = await response.Content.ReadFromJsonAsync<TmdbDiscoverPage>(JsonOptions, ct).ConfigureAwait(false);
            if (pageResult?.Results is not { Count: > 0 } items)
            {
                return Array.Empty<DiscoverResult>();
            }

            var genreMap = await GetGenreMapAsync(mediaType, ct).ConfigureAwait(false);
            _metrics.Increment("tmdb.discover.ok");
            return MapPage(items, mediaType, genreMap);
        }
        catch (Exception ex)
        {
            _metrics.Increment("tmdb.discover.error");
            _logger.LogWarning(ex, "Orca Engine: TMDB discover failed for {Path} page {Page}.", path, safePage);
            return Array.Empty<DiscoverResult>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoverResult>> DiscoverFilteredAsync(MediaType mediaType, TmdbDiscoverFilters filters, int page, CancellationToken ct = default)
    {
        var kind = MediaKind(mediaType);
        if (kind is null || !TryConfig(out var apiKey))
        {
            return Array.Empty<DiscoverResult>();
        }

        var safePage = page < 1 ? 1 : page;
        var query = new List<string>
        {
            $"page={safePage}",
            "sort_by=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(filters.SortBy) ? "popularity.desc" : filters.SortBy),
        };
        if (filters.WithGenres.Count > 0)
        {
            // '|' joins genre ids with OR semantics (',' would mean AND).
            query.Add("with_genres=" + Uri.EscapeDataString(string.Join("|", filters.WithGenres)));
        }

        if (!string.IsNullOrWhiteSpace(filters.WithOriginalLanguage))
        {
            query.Add("with_original_language=" + Uri.EscapeDataString(filters.WithOriginalLanguage));
        }

        if (!string.IsNullOrWhiteSpace(filters.WatchRegion))
        {
            // watch_region only takes effect together with monetization types.
            query.Add("watch_region=" + Uri.EscapeDataString(filters.WatchRegion));
            query.Add("with_watch_monetization_types=" + Uri.EscapeDataString(
                string.IsNullOrWhiteSpace(filters.WithWatchMonetizationTypes) ? "flatrate" : filters.WithWatchMonetizationTypes));
        }

        if (filters.VoteCountGte is { } votes and > 0)
        {
            query.Add("vote_count.gte=" + votes.ToString(CultureInfo.InvariantCulture));
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = Build(HttpMethod.Get, Url(apiKey, $"/discover/{kind}", query.ToArray()), apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("tmdb.discover.error");
                return Array.Empty<DiscoverResult>();
            }

            var pageResult = await response.Content.ReadFromJsonAsync<TmdbDiscoverPage>(JsonOptions, ct).ConfigureAwait(false);
            if (pageResult?.Results is not { Count: > 0 } items)
            {
                return Array.Empty<DiscoverResult>();
            }

            var genreMap = await GetGenreMapAsync(mediaType, ct).ConfigureAwait(false);
            _metrics.Increment("tmdb.discover.ok");
            return MapPage(items, mediaType, genreMap);
        }
        catch (Exception ex)
        {
            _metrics.Increment("tmdb.discover.error");
            _logger.LogWarning(ex, "Orca Engine: TMDB filtered discover failed for {Kind} page {Page}.", kind, safePage);
            return Array.Empty<DiscoverResult>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetKeywordsAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default)
    {
        var kind = MediaKind(mediaType);
        if (kind is null || tmdbId <= 0 || !TryConfig(out var apiKey))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = Build(HttpMethod.Get, Url(apiKey, $"/{kind}/{tmdbId}/keywords"), apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var keywords = await response.Content.ReadFromJsonAsync<TmdbKeywords>(JsonOptions, ct).ConfigureAwait(false);
            return ExtractKeywords(keywords);
        }
        catch (Exception ex)
        {
            _metrics.Increment("tmdb.keywords.error");
            _logger.LogWarning(ex, "Orca Engine: TMDB keywords fetch failed for {Kind} {TmdbId}.", kind, tmdbId);
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoverResult>> GetRelatedAsync(int tmdbId, MediaType mediaType, TmdbRelatedKind kind, CancellationToken ct = default)
    {
        var mk = MediaKind(mediaType);
        if (mk is null || tmdbId <= 0 || !TryConfig(out var apiKey))
        {
            return Array.Empty<DiscoverResult>();
        }

        var leaf = kind == TmdbRelatedKind.Similar ? "similar" : "recommendations";

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = Build(HttpMethod.Get, Url(apiKey, $"/{mk}/{tmdbId}/{leaf}"), apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("tmdb.related.error");
                return Array.Empty<DiscoverResult>();
            }

            var pageResult = await response.Content.ReadFromJsonAsync<TmdbDiscoverPage>(JsonOptions, ct).ConfigureAwait(false);
            if (pageResult?.Results is not { Count: > 0 } items)
            {
                return Array.Empty<DiscoverResult>();
            }

            var genreMap = await GetGenreMapAsync(mediaType, ct).ConfigureAwait(false);
            _metrics.Increment("tmdb.related.ok");
            return MapPage(items, mediaType, genreMap);
        }
        catch (Exception ex)
        {
            _metrics.Increment("tmdb.related.error");
            _logger.LogWarning(ex, "Orca Engine: TMDB {Leaf} fetch failed for {Kind} {TmdbId}.", leaf, mk, tmdbId);
            return Array.Empty<DiscoverResult>();
        }
    }

    /// <inheritdoc />
    public async Task<TmdbUpcomingEpisode?> GetNextEpisodeAsync(int tmdbId, CancellationToken ct = default)
    {
        if (tmdbId <= 0 || !TryConfig(out var apiKey))
        {
            return null;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = Build(HttpMethod.Get, Url(apiKey, $"/tv/{tmdbId}"), apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var detail = await response.Content.ReadFromJsonAsync<TmdbDetail>(JsonOptions, ct).ConfigureAwait(false);
            var next = detail?.NextEpisodeToAir;
            if (next?.AirDate is not { } raw
                || !DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var airDate))
            {
                return null;
            }

            return new TmdbUpcomingEpisode
            {
                SeriesTmdbId = tmdbId,
                SeriesTitle = detail!.Name ?? detail.Title,
                AirDate = airDate,
                SeasonNumber = next.SeasonNumber,
                EpisodeNumber = next.EpisodeNumber,
                EpisodeName = next.Name,
            };
        }
        catch (Exception ex)
        {
            _metrics.Increment("tmdb.upcoming.error");
            _logger.LogWarning(ex, "Orca Engine: TMDB next-episode fetch failed for tv {TmdbId}.", tmdbId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<TmdbProviderBrand?> GetPrimaryProviderAsync(int tmdbId, MediaType mediaType, string region, CancellationToken ct = default)
    {
        var kind = MediaKind(mediaType);
        if (kind is null || tmdbId <= 0 || !TryConfig(out var apiKey))
        {
            return null;
        }

        var reg = string.IsNullOrWhiteSpace(region) ? "US" : region.Trim().ToUpperInvariant();

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = Build(HttpMethod.Get, Url(apiKey, $"/{kind}/{tmdbId}/watch/providers"), apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("tmdb.providers.error");
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<TmdbWatchProvidersResponse>(JsonOptions, ct).ConfigureAwait(false);
            if (payload?.Results is null
                || !payload.Results.TryGetValue(reg, out var regionData)
                || regionData?.Flatrate is not { Count: > 0 } flatrate)
            {
                return null;
            }

            // Lowest display_priority = the region's most prominent streaming brand for this title.
            var primary = flatrate
                .Where(p => p.ProviderId > 0 && !string.IsNullOrWhiteSpace(p.ProviderName))
                .OrderBy(p => p.DisplayPriority)
                .FirstOrDefault();
            if (primary is null)
            {
                return null;
            }

            _metrics.Increment("tmdb.providers.ok");
            return new TmdbProviderBrand
            {
                ProviderId = primary.ProviderId,
                ProviderName = DisplayName(primary.ProviderName!.Trim()),
                LogoPath = primary.LogoPath,
            };
        }
        catch (Exception ex)
        {
            _metrics.Increment("tmdb.providers.error");
            _logger.LogWarning(ex, "Orca Engine: TMDB watch-providers fetch failed for {Kind} {TmdbId}.", kind, tmdbId);
            return null;
        }
    }

    /// <summary>Tidies a few verbose TMDB provider names into their common brand form.</summary>
    private static string DisplayName(string raw) => raw switch
    {
        "Amazon Prime Video" or "Amazon Video" => "Prime Video",
        "Disney Plus" => "Disney+",
        "Apple TV Plus" => "Apple TV+",
        _ => raw,
    };

    /// <summary>Maps a TMDB discover/related page into normalized <see cref="DiscoverResult"/>s.</summary>
    private static List<DiscoverResult> MapPage(List<TmdbDiscoverItem> items, MediaType mediaType, IReadOnlyDictionary<int, string> genreMap)
    {
        var results = new List<DiscoverResult>(items.Count);
        foreach (var item in items)
        {
            if (item.Id <= 0)
            {
                continue;
            }

            results.Add(new DiscoverResult
            {
                TmdbId = item.Id,
                MediaType = mediaType,
                Title = item.Title ?? item.Name ?? string.Empty,
                Overview = item.Overview,
                Year = ParseYear(item.ReleaseDate ?? item.FirstAirDate),
                CommunityRating = item.VoteAverage is { } v ? (float)v : null,
                Popularity = item.Popularity,
                OriginalLanguage = item.OriginalLanguage,
                Genres = item.GenreIds
                    .Select(id => genreMap.TryGetValue(id, out var name) ? name : null)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .ToList(),
                PosterImageUrl = Image(PosterSize, item.PosterPath),
                BackdropImageUrl = Image(BackdropSize, item.BackdropPath),
                Availability = AvailabilityState.Request,
            });
        }

        return results;
    }

    private static List<string> ExtractKeywords(TmdbKeywords? keywords)
    {
        if (keywords is null)
        {
            return new List<string>();
        }

        // Movie keywords are under "keywords", TV under "results" — merge both, de-duped.
        return keywords.Keywords.Concat(keywords.Results)
            .Select(k => k.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractPeople(TmdbCredits? credits)
    {
        // Mirror CatalogMapper's role-prefixed shape ("Actor:X"/"Director:Y"/"Writer:Z"), capping
        // actors so a big cast doesn't dominate the People affinity dimension.
        const int maxActors = 8;
        const int maxPeople = 15;

        var names = new List<string>();
        if (credits is null)
        {
            return names;
        }

        var actors = 0;
        foreach (var c in credits.Cast)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || actors >= maxActors)
            {
                continue;
            }

            names.Add("Actor:" + c.Name!.Trim());
            actors++;
            if (names.Count >= maxPeople)
            {
                return names;
            }
        }

        foreach (var c in credits.Crew)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                continue;
            }

            var role = string.Equals(c.Job, "Director", StringComparison.OrdinalIgnoreCase)
                ? "Director"
                : (string.Equals(c.Job, "Writer", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(c.Job, "Screenplay", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(c.Department, "Writing", StringComparison.OrdinalIgnoreCase))
                    ? "Writer"
                    : null;
            if (role is null)
            {
                continue;
            }

            var entry = $"{role}:{c.Name!.Trim()}";
            if (!names.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(entry);
            }

            if (names.Count >= maxPeople)
            {
                break;
            }
        }

        return names;
    }

    private static string? Image(string size, string? path)
        => string.IsNullOrWhiteSpace(path) ? null : ImageBase + size + path;

    private static string? Trailer(TmdbVideos? videos)
    {
        if (videos?.Results is not { Count: > 0 } list)
        {
            return null;
        }

        var pick = list.FirstOrDefault(v => IsYouTube(v) && IsType(v, "Trailer") && v.Official)
                   ?? list.FirstOrDefault(v => IsYouTube(v) && IsType(v, "Trailer"))
                   ?? list.FirstOrDefault(v => IsYouTube(v) && IsType(v, "Teaser"))
                   ?? list.FirstOrDefault(IsYouTube);

        return pick?.Key is { Length: > 0 } key ? "https://www.youtube.com/watch?v=" + key : null;

        static bool IsYouTube(TmdbVideo v)
            => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.Key);

        static bool IsType(TmdbVideo v, string type)
            => string.Equals(v.Type, type, StringComparison.OrdinalIgnoreCase);
    }

    private static string? MediaKind(MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => "movie",
        MediaType.Series => "tv",
        _ => null,
    };

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
        {
            return null;
        }

        return int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    private static bool IsV4Token(string apiKey) => apiKey.Contains('.', StringComparison.Ordinal);

    private static bool TryConfig(out string apiKey)
    {
        apiKey = string.Empty;
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            return false;
        }

        apiKey = config.TmdbApiKey.Trim();
        return true;
    }

    private static string Url(string apiKey, string path, params string[] query)
    {
        var qs = new List<string>(query);

        // A v3 key authenticates via the api_key query param; a v4 token uses the Bearer header instead.
        if (!IsV4Token(apiKey))
        {
            qs.Insert(0, "api_key=" + Uri.EscapeDataString(apiKey));
        }

        return ApiBase + path + (qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty);
    }

    private static HttpRequestMessage Build(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (IsV4Token(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }
}
