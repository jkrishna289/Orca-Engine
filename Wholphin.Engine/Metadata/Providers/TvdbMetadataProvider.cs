using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Http;

namespace Wholphin.Engine.Metadata.Providers;

/// <summary>
/// TheTVDB v4 — series-first metadata, artwork and trailers. Unlike the other providers it needs a
/// login round trip: the API key is exchanged for a bearer token valid about a month.
/// </summary>
/// <remarks>
/// Legacy v1-v3 keys do not work against v4. A user-supported key additionally needs the subscriber
/// PIN; a company key does not, and sending an empty one is rejected, so the PIN is omitted entirely
/// when unset.
/// </remarks>
public class TvdbMetadataProvider : IMetadataProvider
{
    private const string ApiBase = "https://api4.thetvdb.com/v4";

    /// <summary>Cache key for the bearer token. Never logged and never surfaced in diagnostics.</summary>
    private const string TokenCacheKey = "tvdb:token";

    /// <summary>TVDB tokens last about a month; a day keeps the login rare while surviving key rotation.</summary>
    private static readonly TimeSpan TokenTtl = TimeSpan.FromHours(24);

    // TVDB v4 artwork type ids. Series and movies use different id spaces for the same concepts.
    private const int SeriesPoster = 2;
    private const int SeriesBackground = 3;
    private const int SeriesClearLogo = 23;
    private const int MoviePoster = 14;
    private const int MovieBackground = 15;
    private const int MovieClearLogo = 19;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderGate _gate;
    private readonly ICache _cache;
    private readonly Func<PluginConfiguration?> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvdbMetadataProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="gate">The shared provider gate.</param>
    /// <param name="cache">The L1 cache, used to hold the bearer token.</param>
    /// <param name="config">Configuration accessor; defaults to the live plugin configuration.</param>
    public TvdbMetadataProvider(
        IHttpClientFactory httpClientFactory,
        IProviderGate gate,
        ICache cache,
        Func<PluginConfiguration?>? config = null)
    {
        _httpClientFactory = httpClientFactory;
        _gate = gate;
        _cache = cache;
        _config = config ?? (() => Plugin.Instance?.Configuration);
    }

    /// <inheritdoc />
    public string Name => "tvdb";

    /// <inheritdoc />
    public MetadataCapability Capabilities =>
        MetadataCapability.Core | MetadataCapability.Artwork | MetadataCapability.Logo |
        MetadataCapability.Trailer | MetadataCapability.Episodes;

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config()?.TvdbApiKey);

    /// <inheritdoc />
    public Task<MetadataFragment?> FetchAsync(MediaIdentity identity, MetadataCapability wanted, CancellationToken cancellationToken)
    {
        var config = _config();
        if (string.IsNullOrWhiteSpace(config?.TvdbApiKey) || identity.TvdbId is not > 0)
        {
            return Task.FromResult<MetadataFragment?>(null);
        }

        var isSeries = identity.MediaType == MediaType.Series;
        var segment = isSeries ? "series" : "movies";
        var url = $"{ApiBase}/{segment}/{identity.TvdbId.Value.ToString(CultureInfo.InvariantCulture)}/extended";
        var language = string.IsNullOrWhiteSpace(config.MetadataLanguage) ? "en" : config.MetadataLanguage.Trim();

        return _gate.ExecuteAsync(Name, async ct =>
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);

            var token = await GetTokenAsync(client, config, ct).ConfigureAwait(false);
            if (token is null)
            {
                return null;
            }

            var (body, status) = await ProviderHttp.GetJsonWithStatusAsync<TvdbEnvelope>(client, url, ct, token).ConfigureAwait(false);

            // The cached token outlived its validity (or the key was rotated): log in once more.
            if (status == HttpStatusCode.Unauthorized)
            {
                _cache.Remove(TokenCacheKey);
                token = await GetTokenAsync(client, config, ct).ConfigureAwait(false);
                if (token is null)
                {
                    return null;
                }

                body = await ProviderHttp.GetJsonAsync<TvdbEnvelope>(client, url, ct, token).ConfigureAwait(false);
            }

            return Project(body?.Data, isSeries, language);
        }, cancellationToken);
    }

    /// <summary>Maps a TVDB extended record onto a fragment, or null when it carries nothing usable.</summary>
    /// <param name="data">The record.</param>
    /// <param name="isSeries">Whether the series artwork type ids apply.</param>
    /// <param name="language">The preferred artwork language.</param>
    /// <returns>The fragment, or null.</returns>
    internal static MetadataFragment? Project(TvdbRecord? data, bool isSeries, string language)
    {
        if (data is null)
        {
            return null;
        }

        var poster = PickArtwork(data.Artworks, isSeries ? SeriesPoster : MoviePoster, language)
                     ?? (string.IsNullOrWhiteSpace(data.Image) ? null : new ImageCandidate(data.Image!, 0, 0, null, 0));
        var backdrop = PickArtwork(data.Artworks, isSeries ? SeriesBackground : MovieBackground, language);
        var logo = PickArtwork(data.Artworks, isSeries ? SeriesClearLogo : MovieClearLogo, language);

        var genres = data.Genres?
            .Select(g => g.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList() ?? new List<string>();

        var trailer = data.Trailers?
            .OrderByDescending(t => string.Equals(t.Language, language, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Url)
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

        if (poster is null && backdrop is null && logo is null && genres.Count == 0
            && string.IsNullOrWhiteSpace(data.Overview) && string.IsNullOrWhiteSpace(trailer))
        {
            return null;
        }

        return new MetadataFragment
        {
            Source = "tvdb",
            Overview = string.IsNullOrWhiteSpace(data.Overview) ? null : data.Overview,
            Genres = genres,
            Poster = poster,
            Backdrop = backdrop,
            Logo = logo,
            TrailerUrl = string.IsNullOrWhiteSpace(trailer) ? null : trailer,
        };
    }

    /// <summary>Picks the highest-scoring artwork of one type, preferring the wanted language.</summary>
    /// <param name="artworks">The candidate list.</param>
    /// <param name="type">The TVDB artwork type id.</param>
    /// <param name="language">The preferred language.</param>
    /// <returns>The best candidate, or null.</returns>
    internal static ImageCandidate? PickArtwork(List<TvdbArtwork>? artworks, int type, string language)
    {
        if (artworks is not { Count: > 0 })
        {
            return null;
        }

        // TVDB reports languages as ISO 639-2 ("eng"), so match on the prefix rather than equality.
        var best = artworks
            .Where(a => a.Type == type && !string.IsNullOrWhiteSpace(a.Image))
            .OrderByDescending(a => a.Language is not null && a.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(a => a.Score)
            .FirstOrDefault();

        return best is null
            ? null
            : new ImageCandidate(best.Image!, best.Width, best.Height, best.Language, (int)Math.Clamp(best.Score, 0, int.MaxValue));
    }

    /// <summary>Returns a bearer token, logging in only when the cached one has expired.</summary>
    private async Task<string?> GetTokenAsync(HttpClient client, PluginConfiguration config, CancellationToken ct)
    {
        if (_cache.TryGet<string>(TokenCacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        // A company key must NOT send a pin at all — an empty one is rejected outright.
        var body = string.IsNullOrWhiteSpace(config.TvdbPin)
            ? new TvdbLogin { ApiKey = config.TvdbApiKey.Trim() }
            : new TvdbLogin { ApiKey = config.TvdbApiKey.Trim(), Pin = config.TvdbPin.Trim() };

        using var response = await client.PostAsJsonAsync($"{ApiBase}/login", body, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ProviderRateLimitedException(RateLimitRetry.DelayFor(response));
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var envelope = await response.Content.ReadFromJsonAsync<TvdbLoginEnvelope>(cancellationToken: ct).ConfigureAwait(false);
        var token = envelope?.Data?.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        _cache.Set(TokenCacheKey, token, TokenTtl);
        return token;
    }

    /// <summary>The login request body.</summary>
    internal sealed class TvdbLogin
    {
        /// <summary>Gets or sets the v4 API key.</summary>
        [JsonPropertyName("apikey")]
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Gets or sets the subscriber PIN; omitted entirely when null.</summary>
        [JsonPropertyName("pin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Pin { get; set; }
    }

    /// <summary>The login response envelope.</summary>
    internal sealed class TvdbLoginEnvelope
    {
        /// <summary>Gets or sets the payload.</summary>
        [JsonPropertyName("data")]
        public TvdbToken? Data { get; set; }
    }

    /// <summary>The issued bearer token.</summary>
    internal sealed class TvdbToken
    {
        /// <summary>Gets or sets the token.</summary>
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }

    /// <summary>The standard TVDB response envelope.</summary>
    internal sealed class TvdbEnvelope
    {
        /// <summary>Gets or sets the payload.</summary>
        [JsonPropertyName("data")]
        public TvdbRecord? Data { get; set; }
    }

    /// <summary>An extended series or movie record.</summary>
    internal sealed class TvdbRecord
    {
        /// <summary>Gets or sets the overview.</summary>
        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        /// <summary>Gets or sets the primary image URL.</summary>
        [JsonPropertyName("image")]
        public string? Image { get; set; }

        /// <summary>Gets or sets the genres.</summary>
        [JsonPropertyName("genres")]
        public List<TvdbNamed>? Genres { get; set; }

        /// <summary>Gets or sets the artwork.</summary>
        [JsonPropertyName("artworks")]
        public List<TvdbArtwork>? Artworks { get; set; }

        /// <summary>Gets or sets the trailers.</summary>
        [JsonPropertyName("trailers")]
        public List<TvdbTrailer>? Trailers { get; set; }
    }

    /// <summary>A named TVDB entity (genre, company, …).</summary>
    internal sealed class TvdbNamed
    {
        /// <summary>Gets or sets the name.</summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    /// <summary>One artwork entry.</summary>
    internal sealed class TvdbArtwork
    {
        /// <summary>Gets or sets the image URL.</summary>
        [JsonPropertyName("image")]
        public string? Image { get; set; }

        /// <summary>Gets or sets the artwork type id.</summary>
        [JsonPropertyName("type")]
        public int Type { get; set; }

        /// <summary>Gets or sets the ISO 639-2 language.</summary>
        [JsonPropertyName("language")]
        public string? Language { get; set; }

        /// <summary>Gets or sets the community score.</summary>
        [JsonPropertyName("score")]
        public double Score { get; set; }

        /// <summary>Gets or sets the pixel width.</summary>
        [JsonPropertyName("width")]
        public int Width { get; set; }

        /// <summary>Gets or sets the pixel height.</summary>
        [JsonPropertyName("height")]
        public int Height { get; set; }
    }

    /// <summary>One trailer entry.</summary>
    internal sealed class TvdbTrailer
    {
        /// <summary>Gets or sets the trailer URL.</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>Gets or sets the language.</summary>
        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }
}
