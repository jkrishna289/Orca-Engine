using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Integrations.Arr;

/// <summary>
/// HTTP adapter for <see cref="IArrClient"/>. Reads Sonarr/Radarr base URLs + API keys from the admin
/// plugin configuration and queries each service's v3 calendar. Every call is wrapped so a failure
/// degrades to an empty list instead of throwing.
/// </summary>
public class ArrClient : IArrClient
{
    private const string ApiKeyHeader = "X-Api-Key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<ArrClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory provided by Jellyfin.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    public ArrClient(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<ArrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => SonarrConfigured || RadarrConfigured;

    private static bool SonarrConfigured => Configured(Plugin.Instance?.Configuration?.SonarrUrl, Plugin.Instance?.Configuration?.SonarrApiKey);

    private static bool RadarrConfigured => Configured(Plugin.Instance?.Configuration?.RadarrUrl, Plugin.Instance?.Configuration?.RadarrApiKey);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArrCalendarEntry>> GetCalendarAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var entries = new List<ArrCalendarEntry>();

        if (SonarrConfigured)
        {
            entries.AddRange(await GetSonarrAsync(startUtc, endUtc, ct).ConfigureAwait(false));
        }

        if (RadarrConfigured)
        {
            entries.AddRange(await GetRadarrAsync(startUtc, endUtc, ct).ConfigureAwait(false));
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArrHistoryEvent>> GetHistoryAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        var events = new List<ArrHistoryEvent>();

        if (SonarrConfigured)
        {
            events.AddRange(await GetSonarrHistoryAsync(sinceUtc, ct).ConfigureAwait(false));
        }

        if (RadarrConfigured)
        {
            events.AddRange(await GetRadarrHistoryAsync(sinceUtc, ct).ConfigureAwait(false));
        }

        return events;
    }

    private async Task<IReadOnlyList<ArrHistoryEvent>> GetSonarrHistoryAsync(DateTime sinceUtc, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var baseUrl = config?.SonarrUrl?.Trim().TrimEnd('/');
        var apiKey = config?.SonarrApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<ArrHistoryEvent>();
        }

        var url = $"{baseUrl}/api/v3/history/since?date={Iso(sinceUtc)}&includeSeries=true&includeEpisode=true";

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build(url, apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("arr.sonarr.history.error");
                return Array.Empty<ArrHistoryEvent>();
            }

            var items = await response.Content.ReadFromJsonAsync<List<SonarrHistoryItem>>(JsonOptions, ct).ConfigureAwait(false);
            var result = new List<ArrHistoryEvent>();
            foreach (var item in items ?? new List<SonarrHistoryItem>())
            {
                if (!IsImport(item.EventType) || !TryDate(item.Date, out var occurred))
                {
                    continue;
                }

                result.Add(new ArrHistoryEvent
                {
                    MediaType = MediaType.Series,
                    TmdbId = item.Series?.TmdbId,
                    TvdbId = item.Series?.TvdbId,
                    ImdbId = item.Series?.ImdbId,
                    Title = item.Series?.Title ?? string.Empty,
                    SeasonNumber = item.Episode?.SeasonNumber,
                    EpisodeNumber = item.Episode?.EpisodeNumber,
                    OccurredUtc = occurred,
                });
            }

            _metrics.Increment("arr.sonarr.history.ok");
            return result;
        }
        catch (Exception ex)
        {
            _metrics.Increment("arr.sonarr.history.error");
            _logger.LogWarning(ex, "Orca Engine: Sonarr history fetch failed.");
            return Array.Empty<ArrHistoryEvent>();
        }
    }

    private async Task<IReadOnlyList<ArrHistoryEvent>> GetRadarrHistoryAsync(DateTime sinceUtc, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var baseUrl = config?.RadarrUrl?.Trim().TrimEnd('/');
        var apiKey = config?.RadarrApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<ArrHistoryEvent>();
        }

        var url = $"{baseUrl}/api/v3/history/since?date={Iso(sinceUtc)}&includeMovie=true";

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build(url, apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("arr.radarr.history.error");
                return Array.Empty<ArrHistoryEvent>();
            }

            var items = await response.Content.ReadFromJsonAsync<List<RadarrHistoryItem>>(JsonOptions, ct).ConfigureAwait(false);
            var result = new List<ArrHistoryEvent>();
            foreach (var item in items ?? new List<RadarrHistoryItem>())
            {
                if (!IsImport(item.EventType) || !TryDate(item.Date, out var occurred))
                {
                    continue;
                }

                result.Add(new ArrHistoryEvent
                {
                    MediaType = MediaType.Movie,
                    TmdbId = item.Movie?.TmdbId,
                    ImdbId = item.Movie?.ImdbId,
                    Title = item.Movie?.Title ?? string.Empty,
                    OccurredUtc = occurred,
                });
            }

            _metrics.Increment("arr.radarr.history.ok");
            return result;
        }
        catch (Exception ex)
        {
            _metrics.Increment("arr.radarr.history.error");
            _logger.LogWarning(ex, "Orca Engine: Radarr history fetch failed.");
            return Array.Empty<ArrHistoryEvent>();
        }
    }

    // A completed download ("...FolderImported"). Grabs/renames/deletions aren't availability events.
    private static bool IsImport(string? eventType)
        => !string.IsNullOrWhiteSpace(eventType)
            && eventType.Contains("import", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ArrMonitorState> GetMonitorStateAsync(CancellationToken ct = default)
    {
        var state = new ArrMonitorState();

        if (SonarrConfigured)
        {
            await AddSonarrMonitoredAsync(state, ct).ConfigureAwait(false);
        }

        if (RadarrConfigured)
        {
            await AddRadarrMonitoredAsync(state, ct).ConfigureAwait(false);
        }

        return state;
    }

    private async Task AddSonarrMonitoredAsync(ArrMonitorState state, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var baseUrl = config?.SonarrUrl?.Trim().TrimEnd('/');
        var apiKey = config?.SonarrApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build($"{baseUrl}/api/v3/series", apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("arr.sonarr.monitor.error");
                return;
            }

            var items = await response.Content.ReadFromJsonAsync<List<SonarrSeriesItem>>(JsonOptions, ct).ConfigureAwait(false);
            foreach (var item in items ?? new List<SonarrSeriesItem>())
            {
                if (item.TmdbId is not { } tmdb || tmdb <= 0)
                {
                    continue;
                }

                var series = new ArrMonitoredSeries { Monitored = item.Monitored };
                foreach (var season in item.Seasons ?? new List<SonarrSeasonItem>())
                {
                    if (season.Monitored)
                    {
                        series.MonitoredSeasons.Add(season.SeasonNumber);
                    }
                }

                state.Series[tmdb] = series;
            }

            _metrics.Increment("arr.sonarr.monitor.ok");
        }
        catch (Exception ex)
        {
            _metrics.Increment("arr.sonarr.monitor.error");
            _logger.LogWarning(ex, "Orca Engine: Sonarr series (monitored) fetch failed.");
        }
    }

    private async Task AddRadarrMonitoredAsync(ArrMonitorState state, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var baseUrl = config?.RadarrUrl?.Trim().TrimEnd('/');
        var apiKey = config?.RadarrApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build($"{baseUrl}/api/v3/movie", apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("arr.radarr.monitor.error");
                return;
            }

            var items = await response.Content.ReadFromJsonAsync<List<RadarrMovieItem>>(JsonOptions, ct).ConfigureAwait(false);
            foreach (var item in items ?? new List<RadarrMovieItem>())
            {
                if (item.Monitored && item.TmdbId is { } tmdb && tmdb > 0)
                {
                    state.Movies.Add(tmdb);
                }
            }

            _metrics.Increment("arr.radarr.monitor.ok");
        }
        catch (Exception ex)
        {
            _metrics.Increment("arr.radarr.monitor.error");
            _logger.LogWarning(ex, "Orca Engine: Radarr movie (monitored) fetch failed.");
        }
    }

    private async Task<IReadOnlyList<ArrCalendarEntry>> GetSonarrAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var baseUrl = config?.SonarrUrl?.Trim().TrimEnd('/');
        var apiKey = config?.SonarrApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<ArrCalendarEntry>();
        }

        var url = $"{baseUrl}/api/v3/calendar?start={Iso(startUtc)}&end={Iso(endUtc)}&includeSeries=true&unmonitored=false";

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build(url, apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("arr.sonarr.error");
                return Array.Empty<ArrCalendarEntry>();
            }

            var items = await response.Content.ReadFromJsonAsync<List<SonarrCalendarItem>>(JsonOptions, ct).ConfigureAwait(false);
            var result = new List<ArrCalendarEntry>();
            foreach (var item in items ?? new List<SonarrCalendarItem>())
            {
                if (!TryDate(item.AirDateUtc, out var air))
                {
                    continue;
                }

                result.Add(new ArrCalendarEntry
                {
                    MediaType = MediaType.Series,
                    TmdbId = item.Series?.TmdbId,
                    TvdbId = item.Series?.TvdbId,
                    ImdbId = item.Series?.ImdbId,
                    Title = item.Series?.Title ?? string.Empty,
                    EpisodeTitle = item.Title,
                    SeasonNumber = item.SeasonNumber,
                    EpisodeNumber = item.EpisodeNumber,
                    AirDateUtc = air,
                    HasFile = item.HasFile,
                });
            }

            _metrics.Increment("arr.sonarr.ok");
            return result;
        }
        catch (Exception ex)
        {
            _metrics.Increment("arr.sonarr.error");
            _logger.LogWarning(ex, "Orca Engine: Sonarr calendar fetch failed.");
            return Array.Empty<ArrCalendarEntry>();
        }
    }

    private async Task<IReadOnlyList<ArrCalendarEntry>> GetRadarrAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var baseUrl = config?.RadarrUrl?.Trim().TrimEnd('/');
        var apiKey = config?.RadarrApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<ArrCalendarEntry>();
        }

        var url = $"{baseUrl}/api/v3/calendar?start={Iso(startUtc)}&end={Iso(endUtc)}&unmonitored=false";

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = Build(url, apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("arr.radarr.error");
                return Array.Empty<ArrCalendarEntry>();
            }

            var items = await response.Content.ReadFromJsonAsync<List<RadarrCalendarItem>>(JsonOptions, ct).ConfigureAwait(false);
            var result = new List<ArrCalendarEntry>();
            foreach (var item in items ?? new List<RadarrCalendarItem>())
            {
                // Prefer the home-availability date (digital → physical), then cinema as a last resort.
                if (!TryDate(item.DigitalRelease, out var date)
                    && !TryDate(item.PhysicalRelease, out date)
                    && !TryDate(item.InCinemas, out date))
                {
                    continue;
                }

                result.Add(new ArrCalendarEntry
                {
                    MediaType = MediaType.Movie,
                    TmdbId = item.TmdbId,
                    ImdbId = item.ImdbId,
                    Title = item.Title ?? string.Empty,
                    AirDateUtc = date,
                    HasFile = item.HasFile,
                });
            }

            _metrics.Increment("arr.radarr.ok");
            return result;
        }
        catch (Exception ex)
        {
            _metrics.Increment("arr.radarr.error");
            _logger.LogWarning(ex, "Orca Engine: Radarr calendar fetch failed.");
            return Array.Empty<ArrCalendarEntry>();
        }
    }

    private static bool Configured(string? url, string? key)
        => !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key);

    private static string Iso(DateTime dt)
        => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static bool TryDate(string? raw, out DateTime value)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = default;
        return false;
    }

    private static HttpRequestMessage Build(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(ApiKeyHeader, apiKey);
        return request;
    }
}
