using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Http;

namespace Wholphin.Engine.Metadata.Providers;

/// <summary>
/// OMDb — the only source of real critic scores the engine has. Keyed by IMDb id, which is why
/// <see cref="IMediaIdentityResolver"/> has to run first.
/// </summary>
/// <remarks>
/// Free tier is 1000 requests/day, so this provider is asked only for the Ratings capability and only
/// for rows that are actually missing one.
/// </remarks>
public class OmdbMetadataProvider : IMetadataProvider
{
    private const string ApiBase = "https://www.omdbapi.com/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderGate _gate;
    private readonly Func<PluginConfiguration?> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="OmdbMetadataProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="gate">The shared provider gate.</param>
    /// <param name="config">Configuration accessor; defaults to the live plugin configuration.</param>
    public OmdbMetadataProvider(IHttpClientFactory httpClientFactory, IProviderGate gate, Func<PluginConfiguration?>? config = null)
    {
        _httpClientFactory = httpClientFactory;
        _gate = gate;
        _config = config ?? (() => Plugin.Instance?.Configuration);
    }

    /// <inheritdoc />
    public string Name => "omdb";

    /// <inheritdoc />
    public MetadataCapability Capabilities => MetadataCapability.Ratings;

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config()?.OmdbApiKey);

    /// <inheritdoc />
    public Task<MetadataFragment?> FetchAsync(MediaIdentity identity, MetadataCapability wanted, CancellationToken cancellationToken)
    {
        var apiKey = _config()?.OmdbApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(identity.ImdbId))
        {
            return Task.FromResult<MetadataFragment?>(null);
        }

        var url = $"{ApiBase}?apikey={Uri.EscapeDataString(apiKey)}&i={Uri.EscapeDataString(identity.ImdbId)}";

        return _gate.ExecuteAsync(Name, async ct =>
        {
            using var client = _httpClientFactory.CreateClient(OrcaMetricsHandler.ClientName);
            var response = await ProviderHttp.GetJsonAsync<OmdbResponse>(client, url, ct).ConfigureAwait(false);
            return Project(response);
        }, cancellationToken);
    }

    /// <summary>
    /// Maps an OMDb response onto a fragment, or null when OMDb has nothing.
    /// </summary>
    /// <param name="response">The parsed response.</param>
    /// <returns>The fragment, or null.</returns>
    /// <remarks>
    /// OMDb signals "not found" as HTTP 200 with <c>{"Response":"False"}</c>, so the status code alone
    /// never tells you whether there is data. Treating that as a MISS rather than an error is what
    /// stops a handful of obscure titles from tripping the circuit breaker.
    /// </remarks>
    internal static MetadataFragment? Project(OmdbResponse? response)
    {
        if (response is null || !string.Equals(response.Response, "True", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ratings = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var rating in response.Ratings ?? new List<OmdbRating>())
        {
            var key = SourceKey(rating.Source);
            if (key is not null && ParseRating(rating.Value) is { } value)
            {
                ratings[key] = value;
            }
        }

        // The top-level fields are the fallback: the Ratings array occasionally omits a source that
        // is present here. They need explicit scales — imdbRating is bare and out of TEN ("8.7")
        // while Metascore is bare and out of a HUNDRED ("74"), so the notation cannot tell them apart.
        if (!ratings.ContainsKey("imdb") && ParseRating(response.ImdbRating) is { } imdb)
        {
            ratings["imdb"] = Bare(response.ImdbRating) ? Math.Round(imdb * 10, 1) : imdb;
        }

        if (!ratings.ContainsKey("metacritic") && ParseRating(response.Metascore) is { } meta)
        {
            ratings["metacritic"] = meta;
        }

        return ratings.Count == 0
            ? null
            : new MetadataFragment { Source = "omdb", Ratings = ratings };
    }

    /// <summary>Normalizes an OMDb rating string to a 0-100 scale.</summary>
    /// <param name="value">"8.7/10", "73%", "74/100", "74", or "N/A".</param>
    /// <returns>The 0-100 score, or null when unusable.</returns>
    internal static double? ParseRating(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = value.Trim();

        if (text.EndsWith('%'))
        {
            return Number(text[..^1]);
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            var numerator = Number(text[..slash]);
            var denominator = Number(text[(slash + 1)..]);
            return numerator is { } n && denominator is > 0 ? Math.Round(n / denominator.Value * 100, 1) : null;
        }

        // A bare number is Metascore's form, already 0-100.
        return Number(text);

        static double? Number(string s)
            => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>Whether a value carries no scale of its own ("8.7" rather than "8.7/10" or "73%").</summary>
    private static bool Bare(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.Contains('/', StringComparison.Ordinal)
           && !value.Contains('%', StringComparison.Ordinal);

    private static string? SourceKey(string? source) => source switch
    {
        "Internet Movie Database" => "imdb",
        "Rotten Tomatoes" => "rt",
        "Metacritic" => "metacritic",
        _ => null,
    };

    /// <summary>The OMDb lookup response.</summary>
    internal sealed class OmdbResponse
    {
        /// <summary>Gets or sets "True" or "False" — OMDb reports "not found" at HTTP 200.</summary>
        [JsonPropertyName("Response")]
        public string? Response { get; set; }

        /// <summary>Gets or sets the IMDb rating out of 10.</summary>
        [JsonPropertyName("imdbRating")]
        public string? ImdbRating { get; set; }

        /// <summary>Gets or sets the Metacritic score out of 100.</summary>
        [JsonPropertyName("Metascore")]
        public string? Metascore { get; set; }

        /// <summary>Gets or sets the per-source ratings.</summary>
        [JsonPropertyName("Ratings")]
        public List<OmdbRating>? Ratings { get; set; }
    }

    /// <summary>One entry of OMDb's Ratings array.</summary>
    internal sealed class OmdbRating
    {
        /// <summary>Gets or sets the rating source name.</summary>
        [JsonPropertyName("Source")]
        public string? Source { get; set; }

        /// <summary>Gets or sets the rating in that source's own notation.</summary>
        [JsonPropertyName("Value")]
        public string? Value { get; set; }
    }
}
