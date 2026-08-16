using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Streaming;

namespace Wholphin.Engine.Integrations.Prowlarr;

/// <summary>Finds candidate stream sources for a title.</summary>
public interface IProwlarrClient
{
    /// <summary>Gets a value indicating whether a Prowlarr URL and API key are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Searches the configured indexers.</summary>
    /// <param name="query">Free-text query, usually "Title Year" or "Title SxxEyy".</param>
    /// <param name="movie">True to search movie categories, false for TV.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Unscored candidates; empty on any failure.</returns>
    Task<IReadOnlyList<TorrentSource>> SearchAsync(string query, bool movie, CancellationToken cancellationToken);
}

/// <summary>
/// Prowlarr adapter. Prowlarr's only job here is finding candidates — it never touches playback,
/// buffering or download clients. Fails soft to an empty list so an unreachable or misconfigured
/// indexer stack shows up as "no sources found" rather than an error.
/// </summary>
public class ProwlarrClient : IProwlarrClient
{
    private const string ApiKeyHeader = "X-Api-Key";

    // Prowlarr's newznab-style category numbers: 2000 = Movies, 5000 = TV.
    private const string MovieCategories = "2000";
    private const string TvCategories = "5000";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<ProwlarrClient> _logger;

    /// <summary>Initializes a new instance of the <see cref="ProwlarrClient"/> class.</summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">Logger.</param>
    public ProwlarrClient(IHttpClientFactory httpClientFactory, IEngineMetrics metrics, ILogger<ProwlarrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => TryConfig(out _, out _);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TorrentSource>> SearchAsync(
        string query,
        bool movie,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || !TryConfig(out var baseUrl, out var apiKey))
        {
            return Array.Empty<TorrentSource>();
        }

        var url = $"{baseUrl}/api/v1/search"
            + $"?query={Uri.EscapeDataString(query)}"
            + $"&categories={(movie ? MovieCategories : TvCategories)}"
            + "&type=search"
            + "&limit=200";

        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(ApiKeyHeader, apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("prowlarr.search.error");
                _logger.LogWarning(
                    "Orca Engine: Prowlarr search returned {Status} for {Query}.",
                    (int)response.StatusCode,
                    query);
                return Array.Empty<TorrentSource>();
            }

            var results = await response.Content
                .ReadFromJsonAsync<List<ProwlarrRelease>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (results is null || results.Count == 0)
            {
                return Array.Empty<TorrentSource>();
            }

            // Private trackers are never offered for streaming. Streaming reads scattered pieces and
            // stops when the viewer does — on a private tracker that is hit-and-run, which wrecks
            // ratio and gets the operator's account banned.
            //
            // Fails CLOSED: if the indexer list can't be read we cannot prove an indexer is public, so
            // nothing is offered. "No sources" is a recoverable annoyance; streaming someone's private
            // tracker membership into a ban is not.
            var publicIndexers = await PublicIndexerIdsAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
            if (publicIndexers is null)
            {
                _logger.LogWarning(
                    "Orca Engine: could not read Prowlarr's indexer list — refusing all sources rather than "
                    + "risk streaming from a private tracker.");
                return Array.Empty<TorrentSource>();
            }

            var beforePrivacy = results.Count;
            results = results.Where(r => r.IndexerId is int id && publicIndexers.Contains(id)).ToList();
            var droppedPrivate = beforePrivacy - results.Count;

            var mapped = results
                .Select(Map)
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            if (droppedPrivate > 0)
            {
                _metrics.Increment("prowlarr.search.dropped.private", droppedPrivate);
                _logger.LogInformation(
                    "Orca Engine: dropped {Dropped} result(s) from private/semi-private indexers.",
                    droppedPrivate);
            }

            // Counted so the Observatory can show the privacy filter working rather than asserting it.
            _metrics.Increment("prowlarr.search.ok");
            _metrics.Increment("prowlarr.search.results", mapped.Count);

            // "usable" means it had a magnet OR a fetchable .torrent link — an earlier version of this
            // message said "with a magnet", which made the log look like every result was a magnet
            // when most were proxy links.
            _logger.LogInformation(
                "Orca Engine: Prowlarr returned {Raw} results ({Usable} usable, {Magnets} of them magnets) for {Query}.",
                results.Count,
                mapped.Count,
                mapped.Count(s => s.DownloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)),
                query);

            return mapped;
        }
        catch (Exception ex)
        {
            _metrics.Increment("prowlarr.search.error");
            _logger.LogWarning(ex, "Orca Engine: Prowlarr search failed for {Query}.", query);
            return Array.Empty<TorrentSource>();
        }
    }

    /// <summary>
    /// Ids of the indexers Prowlarr reports as public.
    /// </summary>
    /// <param name="baseUrl">Prowlarr base URL.</param>
    /// <param name="apiKey">Prowlarr API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The public indexer ids, or null when the list could not be read at all.</returns>
    /// <remarks>
    /// Null is deliberately distinct from empty: empty means "asked, and none are public", while null
    /// means "could not ask", and only the caller can decide that the safe response to not knowing is
    /// to offer nothing. Anything Prowlarr does not explicitly call "public" — including
    /// <c>semiPrivate</c> — is treated as private.
    /// </remarks>
    private async Task<HashSet<int>?> PublicIndexerIdsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/indexer");
            request.Headers.Add(ApiKeyHeader, apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var indexers = await response.Content
                .ReadFromJsonAsync<List<ProwlarrIndexer>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (indexers is null)
            {
                return null;
            }

            return indexers
                .Where(i => string.Equals(i.Privacy, "public", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Id)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: reading Prowlarr's indexer list failed.");
            return null;
        }
    }

    private static TorrentSource? Map(ProwlarrRelease r)
    {
        if (string.IsNullOrWhiteSpace(r.Title))
        {
            return null;
        }

        // Prefer a real magnet, then any of the other fields that happens to hold one, and only then
        // fall back to an HTTP .torrent link.
        //
        // Each candidate is scheme-checked. An earlier version trusted magnetUrl merely for being
        // non-empty, and Prowlarr populates it with an http:// proxy link — which parsed as neither a
        // magnet nor anything the torrent engine could open, so every source failed to play.
        var magnet = FirstMagnet(r.MagnetUrl, r.DownloadUrl, r.Guid);
        var url = magnet ?? FirstHttp(r.DownloadUrl, r.MagnetUrl, r.Guid);

        if (url is null)
        {
            return null;
        }

        return new TorrentSource
        {
            Title = r.Title!,
            DownloadUrl = url,
            // Derived from the URL so the same release always gets the same handle, which lets a
            // session open minutes after the search without needing the search result again.
            Id = HandleFor(url),
            SizeBytes = r.Size ?? 0,
            Seeders = r.Seeders ?? 0,
            Leechers = r.Leechers ?? 0,
            Indexer = r.Indexer,
            InfoHash = r.InfoHash,
        };
    }

    private static string? FirstMagnet(params string?[] candidates) =>
        candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && c!.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));

    private static string? FirstHttp(params string?[] candidates) =>
        candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && c!.StartsWith("http", StringComparison.OrdinalIgnoreCase));

    private static string HandleFor(string url)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    private static bool TryConfig(out string baseUrl, out string apiKey)
    {
        baseUrl = string.Empty;
        apiKey = string.Empty;

        var config = Plugin.Instance?.Configuration;
        if (config is null
            || string.IsNullOrWhiteSpace(config.ProwlarrUrl)
            || string.IsNullOrWhiteSpace(config.ProwlarrApiKey))
        {
            return false;
        }

        baseUrl = config.ProwlarrUrl.Trim().TrimEnd('/');
        apiKey = config.ProwlarrApiKey.Trim();
        return true;
    }

    /// <summary>A single Prowlarr search result (only the fields ranking needs).</summary>
    private sealed class ProwlarrRelease
    {
        public string? Title { get; set; }

        public long? Size { get; set; }

        public int? Seeders { get; set; }

        [JsonPropertyName("leechers")]
        public int? Leechers { get; set; }

        public string? Indexer { get; set; }

        /// <summary>Gets or sets which indexer produced this, used to look its privacy up.</summary>
        public int? IndexerId { get; set; }

        public string? MagnetUrl { get; set; }

        public string? DownloadUrl { get; set; }

        public string? Guid { get; set; }

        /// <summary>
        /// Gets or sets the torrent's infohash, when the indexer reported one.
        /// </summary>
        /// <remarks>
        /// Worth having even though it is often absent: without it, a result whose only link is an
        /// HTTP proxy cannot have its swarm measured without fetching the .torrent, and those are
        /// exactly the results whose seeder counts proved least trustworthy.
        /// </remarks>
        public string? InfoHash { get; set; }
    }

    /// <summary>One configured indexer, reduced to the privacy decision.</summary>
    private sealed class ProwlarrIndexer
    {
        public int Id { get; set; }

        public string? Name { get; set; }

        /// <summary>Gets or sets Prowlarr's classification: <c>public</c>, <c>private</c> or <c>semiPrivate</c>.</summary>
        public string? Privacy { get; set; }
    }
}
