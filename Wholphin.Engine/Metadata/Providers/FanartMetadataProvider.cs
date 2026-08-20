using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Http;

namespace Wholphin.Engine.Metadata.Providers;

/// <summary>
/// Fanart.tv — community artwork, and the engine's only source of clear logos (the transparent title
/// treatment the client's hero cards want and nothing has ever populated).
/// </summary>
/// <remarks>
/// Movies are addressed by TMDB id, but SERIES are addressed by TVDB id. That asymmetry is why
/// external-id resolution is load-bearing rather than a nicety: without a TVDB id, this provider
/// cannot be asked about a series at all, however it is configured.
/// </remarks>
public class FanartMetadataProvider : IMetadataProvider
{
    private const string ApiBase = "https://webservice.fanart.tv/v3";

    // Fanart enforces fixed dimensions per artwork type, so these are facts about the source rather
    // than guesses — and they are what lets a Fanart poster outrank TMDB's w500 in the merge.
    private const int PosterWidth = 1000;
    private const int PosterHeight = 1500;
    private const int BackgroundWidth = 1920;
    private const int BackgroundHeight = 1080;
    private const int LogoWidth = 800;
    private const int LogoHeight = 310;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderGate _gate;
    private readonly Func<PluginConfiguration?> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="FanartMetadataProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="gate">The shared provider gate.</param>
    /// <param name="config">Configuration accessor; defaults to the live plugin configuration.</param>
    public FanartMetadataProvider(IHttpClientFactory httpClientFactory, IProviderGate gate, Func<PluginConfiguration?>? config = null)
    {
        _httpClientFactory = httpClientFactory;
        _gate = gate;
        _config = config ?? (() => Plugin.Instance?.Configuration);
    }

    /// <inheritdoc />
    public string Name => "fanart";

    /// <inheritdoc />
    public MetadataCapability Capabilities => MetadataCapability.Artwork | MetadataCapability.Logo;

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config()?.FanartApiKey);

    /// <inheritdoc />
    public Task<MetadataFragment?> FetchAsync(MediaIdentity identity, MetadataCapability wanted, CancellationToken cancellationToken)
    {
        var config = _config();
        var apiKey = config?.FanartApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult<MetadataFragment?>(null);
        }

        // Series: TVDB id only. Movies: TMDB id, or the IMDb id Fanart also accepts.
        var isSeries = identity.MediaType == MediaType.Series;
        var id = isSeries
            ? identity.TvdbId is > 0 ? identity.TvdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null
            : identity.TmdbId is > 0 ? identity.TmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : identity.ImdbId;

        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult<MetadataFragment?>(null);
        }

        var segment = isSeries ? "tv" : "movies";
        var url = $"{ApiBase}/{segment}/{Uri.EscapeDataString(id)}?api_key={Uri.EscapeDataString(apiKey)}";
        var language = string.IsNullOrWhiteSpace(config?.MetadataLanguage) ? "en" : config!.MetadataLanguage.Trim();

        return _gate.ExecuteAsync(Name, async ct =>
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            var response = await ProviderHttp.GetJsonAsync<FanartResponse>(client, url, ct).ConfigureAwait(false);
            return Project(response, isSeries, language);
        }, cancellationToken);
    }

    /// <summary>Maps a Fanart response onto a fragment, or null when it carries no usable art.</summary>
    /// <param name="response">The parsed response.</param>
    /// <param name="isSeries">Whether the TV artwork keys apply.</param>
    /// <param name="language">The preferred artwork language.</param>
    /// <returns>The fragment, or null.</returns>
    internal static MetadataFragment? Project(FanartResponse? response, bool isSeries, string language)
    {
        if (response is null)
        {
            return null;
        }

        var poster = PickImage(isSeries ? response.TvPoster : response.MoviePoster, language, PosterWidth, PosterHeight);
        var backdrop = PickImage(isSeries ? response.ShowBackground : response.MovieBackground, language, BackgroundWidth, BackgroundHeight);

        // HD logos first; the SD list is the fallback for titles that only ever got one.
        var logo = PickImage(isSeries ? response.HdTvLogo : response.HdMovieLogo, language, LogoWidth, LogoHeight)
                   ?? PickImage(isSeries ? response.ClearLogo : response.MovieLogo, language, LogoWidth, LogoHeight);

        if (poster is null && backdrop is null && logo is null)
        {
            return null;
        }

        return new MetadataFragment
        {
            Source = "fanart",
            Poster = poster,
            Backdrop = backdrop,
            Logo = logo,
        };
    }

    /// <summary>
    /// Picks the best artwork from one Fanart list: preferred language first, then community likes.
    /// </summary>
    /// <param name="images">The candidate list.</param>
    /// <param name="language">The preferred language.</param>
    /// <param name="width">The nominal width for this artwork type.</param>
    /// <param name="height">The nominal height for this artwork type.</param>
    /// <returns>The best candidate, or null when the list is empty.</returns>
    /// <remarks>
    /// "00" is Fanart's marker for textless art. It ranks below an exact language match (a viewer
    /// wants the title in their language) but above a wrong-language one.
    /// </remarks>
    internal static ImageCandidate? PickImage(List<FanartImage>? images, string language, int width, int height)
    {
        if (images is not { Count: > 0 })
        {
            return null;
        }

        var best = images
            .Where(i => !string.IsNullOrWhiteSpace(i.Url))
            .OrderByDescending(i => LanguageRank(i.Lang, language))
            .ThenByDescending(i => ProviderHttp.ToInt(i.Likes))
            .FirstOrDefault();

        return best is null
            ? null
            : new ImageCandidate(best.Url!, width, height, best.Lang, ProviderHttp.ToInt(best.Likes));
    }

    private static int LanguageRank(string? lang, string preferred)
    {
        if (string.Equals(lang, preferred, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return string.IsNullOrWhiteSpace(lang) || lang == "00" ? 1 : 0;
    }

    /// <summary>One Fanart artwork entry. Fanart encodes <c>likes</c> as a JSON string.</summary>
    internal sealed class FanartImage
    {
        /// <summary>Gets or sets the absolute image URL.</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>Gets or sets the ISO 639-1 language of burned-in text, or "00" for textless.</summary>
        [JsonPropertyName("lang")]
        public string? Lang { get; set; }

        /// <summary>Gets or sets the community likes, as the string Fanart sends.</summary>
        [JsonPropertyName("likes")]
        public string? Likes { get; set; }
    }

    /// <summary>The Fanart v3 response. Movie and TV keys differ, so both sets are declared.</summary>
    internal sealed class FanartResponse
    {
        /// <summary>Gets or sets the HD movie logos.</summary>
        [JsonPropertyName("hdmovielogo")]
        public List<FanartImage>? HdMovieLogo { get; set; }

        /// <summary>Gets or sets the SD movie logos.</summary>
        [JsonPropertyName("movielogo")]
        public List<FanartImage>? MovieLogo { get; set; }

        /// <summary>Gets or sets the movie posters.</summary>
        [JsonPropertyName("movieposter")]
        public List<FanartImage>? MoviePoster { get; set; }

        /// <summary>Gets or sets the movie backgrounds.</summary>
        [JsonPropertyName("moviebackground")]
        public List<FanartImage>? MovieBackground { get; set; }

        /// <summary>Gets or sets the HD series logos.</summary>
        [JsonPropertyName("hdtvlogo")]
        public List<FanartImage>? HdTvLogo { get; set; }

        /// <summary>Gets or sets the SD series logos.</summary>
        [JsonPropertyName("clearlogo")]
        public List<FanartImage>? ClearLogo { get; set; }

        /// <summary>Gets or sets the series posters.</summary>
        [JsonPropertyName("tvposter")]
        public List<FanartImage>? TvPoster { get; set; }

        /// <summary>Gets or sets the series backgrounds.</summary>
        [JsonPropertyName("showbackground")]
        public List<FanartImage>? ShowBackground { get; set; }
    }
}
