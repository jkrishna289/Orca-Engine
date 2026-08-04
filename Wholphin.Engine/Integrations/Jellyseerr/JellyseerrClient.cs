using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Integrations.Jellyseerr;

/// <summary>
/// HTTP adapter for <see cref="IJellyseerrClient"/>. Reads the Jellyseerr URL + API key from the
/// admin plugin configuration and talks to the Jellyseerr v1 REST API. Every call is wrapped so a
/// failure degrades to a neutral result (null / false / empty) instead of throwing.
/// </summary>
public class JellyseerrClient : IJellyseerrClient
{
    private const string ApiKeyHeader = "X-Api-Key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly TimeSpan UserMapTtl = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Wholphin.Engine.Diagnostics.IEngineMetrics _metrics;
    private readonly ILogger<JellyseerrClient> _logger;

    // Cached Jellyfin-user-id (dash-less) → Jellyseerr-user-id map for request attribution.
    private volatile Dictionary<string, int>? _userMap;
    private DateTime _userMapFetchedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory provided by Jellyfin.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public JellyseerrClient(IHttpClientFactory httpClientFactory, Wholphin.Engine.Diagnostics.IEngineMetrics metrics, ILogger<JellyseerrClient> logger)
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
            return config is not null
                   && !string.IsNullOrWhiteSpace(config.JellyseerrUrl)
                   && !string.IsNullOrWhiteSpace(config.JellyseerrApiKey);
        }
    }

    /// <inheritdoc />
    public async Task<AvailabilityState?> GetAvailabilityAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default)
    {
        var kind = MediaKind(mediaType);
        if (kind is null || !TryConfig(out var baseUrl, out var apiKey))
        {
            return null;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build(HttpMethod.Get, $"{baseUrl}/api/v1/{kind}/{tmdbId}", apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var detail = await response.Content
                .ReadFromJsonAsync<JellyseerrDetail>(JsonOptions, ct)
                .ConfigureAwait(false);
            return JellyseerrStatus.ToAvailability(detail?.MediaInfo?.Status);
        }
        catch (Exception ex)
        {
            _metrics.Increment("jellyseerr.availability.error");
            _logger.LogWarning(ex, "Orca Engine: Jellyseerr availability lookup failed for {Kind} {TmdbId}.", kind, tmdbId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RequestAsync(int tmdbId, MediaType mediaType, Guid jellyfinUserId, CancellationToken ct = default)
    {
        var kind = MediaKind(mediaType);
        if (kind is null || !TryConfig(out var baseUrl, out var apiKey))
        {
            return false;
        }

        try
        {
            var body = new Dictionary<string, object>
            {
                ["mediaType"] = kind,
                ["mediaId"] = tmdbId,
            };
            if (kind == "tv")
            {
                // Jellyseerr requires an explicit season selection for series requests.
                body["seasons"] = "all";
            }

            // Attribute the request to the actual user when they map to a Jellyseerr account
            // (so it shows under their name + counts against their quota); else the API-key owner.
            var seerrUserId = await ResolveSeerrUserIdAsync(jellyfinUserId, ct).ConfigureAwait(false);
            if (seerrUserId is { } sid)
            {
                body["userId"] = sid;
            }

            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build(HttpMethod.Post, $"{baseUrl}/api/v1/request", apiKey);
            request.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _metrics.Increment("jellyseerr.request.ok");
                return true;
            }

            _metrics.Increment("jellyseerr.request.error");
            _logger.LogWarning(
                "Orca Engine: Jellyseerr request for {Kind} {TmdbId} returned {Status}.",
                kind,
                tmdbId,
                (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _metrics.Increment("jellyseerr.request.error");
            _logger.LogWarning(ex, "Orca Engine: Jellyseerr request failed for {Kind} {TmdbId}.", kind, tmdbId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoverResult>> DiscoverAsync(MediaType mediaType, int page, CancellationToken ct = default)
    {
        var kind = MediaKind(mediaType);
        if (kind is null || !TryConfig(out var baseUrl, out var apiKey))
        {
            return Array.Empty<DiscoverResult>();
        }

        var path = kind == "movie" ? "discover/movies" : "discover/tv";
        var safePage = page < 1 ? 1 : page;

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build(HttpMethod.Get, $"{baseUrl}/api/v1/{path}?page={safePage}", apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<DiscoverResult>();
            }

            var pageResult = await response.Content
                .ReadFromJsonAsync<JellyseerrDiscoverPage>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (pageResult?.Results is not { Count: > 0 } items)
            {
                return Array.Empty<DiscoverResult>();
            }

            var results = new List<DiscoverResult>(items.Count);
            foreach (var item in items)
            {
                results.Add(new DiscoverResult
                {
                    TmdbId = item.Id,
                    MediaType = mediaType,
                    Title = item.Title ?? item.Name ?? string.Empty,
                    Overview = item.Overview,
                    Year = ParseYear(item.ReleaseDate ?? item.FirstAirDate),
                    CommunityRating = item.VoteAverage is { } v ? (float)v : null,
                    Availability = JellyseerrStatus.ToAvailability(item.MediaInfo?.Status),
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _metrics.Increment("jellyseerr.discover.error");
            _logger.LogWarning(ex, "Orca Engine: Jellyseerr discover failed for {Path} page {Page}.", path, safePage);
            return Array.Empty<DiscoverResult>();
        }
    }

    private async Task<int?> ResolveSeerrUserIdAsync(Guid jellyfinUserId, CancellationToken ct)
    {
        if (jellyfinUserId == Guid.Empty)
        {
            return null;
        }

        var map = await GetUserMapAsync(ct).ConfigureAwait(false);
        return map is not null && map.TryGetValue(jellyfinUserId.ToString("N"), out var id) ? id : null;
    }

    private async Task<Dictionary<string, int>?> GetUserMapAsync(CancellationToken ct)
    {
        var cached = _userMap;
        if (cached is not null && DateTime.UtcNow - _userMapFetchedAt < UserMapTtl)
        {
            return cached;
        }

        if (!TryConfig(out var baseUrl, out var apiKey))
        {
            return cached;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build(HttpMethod.Get, $"{baseUrl}/api/v1/user?take=200", apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return cached;
            }

            var page = await response.Content
                .ReadFromJsonAsync<JellyseerrUserPage>(JsonOptions, ct)
                .ConfigureAwait(false);

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in page?.Results ?? new List<JellyseerrUser>())
            {
                if (!string.IsNullOrWhiteSpace(user.JellyfinUserId))
                {
                    map[user.JellyfinUserId.Replace("-", string.Empty)] = user.Id;
                }
            }

            _userMap = map;
            _userMapFetchedAt = DateTime.UtcNow;
            return map;
        }
        catch (Exception ex)
        {
            _metrics.Increment("jellyseerr.user.error");
            _logger.LogWarning(ex, "Orca Engine: Jellyseerr user-map fetch failed.");
            return cached;
        }
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

    private static bool TryConfig(out string baseUrl, out string apiKey)
    {
        baseUrl = string.Empty;
        apiKey = string.Empty;

        var config = Plugin.Instance?.Configuration;
        if (config is null
            || string.IsNullOrWhiteSpace(config.JellyseerrUrl)
            || string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
        {
            return false;
        }

        baseUrl = config.JellyseerrUrl.Trim().TrimEnd('/');
        apiKey = config.JellyseerrApiKey.Trim();
        return true;
    }

    private static HttpRequestMessage Build(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ApiKeyHeader, apiKey);
        return request;
    }
}
