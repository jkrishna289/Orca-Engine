using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Http;
using Wholphin.Engine.Metadata;
using Wholphin.Engine.Metadata.Providers;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The individual providers: their response parsing, and that every failure mode degrades to null
/// rather than throwing into a metadata request.
/// </summary>
public class MetadataProviderTests
{
    private static readonly MediaIdentity Movie =
        new(157336, "tt0816692", null, null, MediaType.Movie, "Interstellar", null, 2014);

    private static readonly MediaIdentity Series =
        new(1399, "tt0944947", 121361, null, MediaType.Series, "Game of Thrones", null, 2011);

    private static ProviderGate NewGate(PluginConfiguration config)
        => new(new RecordingMetrics(), NullLogger<ProviderGate>.Instance, () => config);

    // --- OMDb ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("8.7/10", 87)]
    [InlineData("73%", 73)]
    [InlineData("74/100", 74)]
    [InlineData("74", 74)]
    public void Omdb_ParseRating_NormalizesEveryNotationTo0To100(string raw, double expected)
    {
        Assert.Equal(expected, OmdbMetadataProvider.ParseRating(raw));
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a rating")]
    public void Omdb_ParseRating_RejectsUnusableValues(string? raw)
    {
        Assert.Null(OmdbMetadataProvider.ParseRating(raw));
    }

    [Fact]
    public void Omdb_Project_ReadsEveryCriticSource()
    {
        var fragment = OmdbMetadataProvider.Project(new OmdbMetadataProvider.OmdbResponse
        {
            Response = "True",
            ImdbRating = "8.7",
            Metascore = "74",
            Ratings = new()
            {
                new() { Source = "Internet Movie Database", Value = "8.7/10" },
                new() { Source = "Rotten Tomatoes", Value = "73%" },
                new() { Source = "Metacritic", Value = "74/100" },
            },
        });

        Assert.Equal(87, fragment!.Ratings["imdb"]);
        Assert.Equal(73, fragment.Ratings["rt"]);
        Assert.Equal(74, fragment.Ratings["metacritic"]);
    }

    [Fact]
    public void Omdb_Project_TreatsResponseFalseAsAMiss_NotAFailure()
    {
        // OMDb reports "not found" as HTTP 200 with Response:"False". Counting that as an error would
        // trip the circuit breaker over a handful of obscure titles.
        Assert.Null(OmdbMetadataProvider.Project(new OmdbMetadataProvider.OmdbResponse { Response = "False" }));
    }

    [Fact]
    public void Omdb_Project_FallsBackToTheTopLevelFieldsWhenTheArrayOmitsASource()
    {
        var fragment = OmdbMetadataProvider.Project(new OmdbMetadataProvider.OmdbResponse
        {
            Response = "True",
            ImdbRating = "8.7",
            Metascore = "74",
            Ratings = new(),
        });

        Assert.Equal(87, fragment!.Ratings["imdb"]);
        Assert.Equal(74, fragment.Ratings["metacritic"]);
    }

    [Fact]
    public void Omdb_Project_ReturnsNullWhenNoScoreIsUsable()
    {
        var fragment = OmdbMetadataProvider.Project(new OmdbMetadataProvider.OmdbResponse
        {
            Response = "True",
            ImdbRating = "N/A",
            Metascore = "N/A",
        });

        Assert.Null(fragment);
    }

    [Fact]
    public async Task Omdb_IsNeverCalledWithoutAnImdbId()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var config = new PluginConfiguration { OmdbApiKey = "key" };
        var provider = new OmdbMetadataProvider(new StubHttpClientFactory(handler), NewGate(config), () => config);

        var noImdb = Movie with { ImdbId = null };
        Assert.Null(await provider.FetchAsync(noImdb, MetadataCapability.Ratings, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Omdb_IsDormantUntilAKeyIsSet()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var config = new PluginConfiguration();
        var provider = new OmdbMetadataProvider(new StubHttpClientFactory(handler), NewGate(config), () => config);

        Assert.False(provider.IsConfigured);
        Assert.Null(await provider.FetchAsync(Movie, MetadataCapability.Ratings, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Omdb_ReturnsRatingsFromALiveResponse()
    {
        const string Body = """
            {"Title":"Interstellar","Response":"True","imdbRating":"8.7","Metascore":"74",
             "Ratings":[{"Source":"Rotten Tomatoes","Value":"73%"}]}
            """;
        var config = new PluginConfiguration { OmdbApiKey = "key" };
        var provider = new OmdbMetadataProvider(
            new StubHttpClientFactory(new FakeHttpHandler(HttpStatusCode.OK, Body)), NewGate(config), () => config);

        var fragment = await provider.FetchAsync(Movie, MetadataCapability.Ratings, CancellationToken.None);

        Assert.Equal(73, fragment!.Ratings["rt"]);
        Assert.Equal("omdb", fragment.Source);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "this is not json")]
    [InlineData(HttpStatusCode.OK, "")]
    public async Task Omdb_FailsSoftOnEveryBadResponse(HttpStatusCode status, string body)
    {
        var config = new PluginConfiguration { OmdbApiKey = "key" };
        var provider = new OmdbMetadataProvider(
            new StubHttpClientFactory(new FakeHttpHandler(status, body)), NewGate(config), () => config);

        Assert.Null(await provider.FetchAsync(Movie, MetadataCapability.Ratings, CancellationToken.None));
    }

    [Fact]
    public async Task Omdb_RateLimiting_IsNotCountedAsAFailure()
    {
        var config = new PluginConfiguration { OmdbApiKey = "key" };
        var gate = NewGate(config);
        var provider = new OmdbMetadataProvider(
            new StubHttpClientFactory(FakeHttpHandler.RateLimited(3)), gate, () => config);

        Assert.Null(await provider.FetchAsync(Movie, MetadataCapability.Ratings, CancellationToken.None));

        var health = gate.Snapshot(new[] { "omdb" }).Single();
        Assert.Equal(1, health.RateLimited);
        Assert.Equal(0, health.Failure);
    }

    [Fact]
    public async Task Omdb_AnExhaustedQuota_CountsAsAFailure_SoTheBreakerStopsTheBleeding()
    {
        // OMDb answers a spent daily allowance with 401 and a "Request limit reached" body. If that
        // read as "no data for this title", the enricher would mark thousands of rows as enriched
        // with nothing and not revisit them for a month.
        var config = new PluginConfiguration { OmdbApiKey = "key" };
        var gate = NewGate(config);
        var provider = new OmdbMetadataProvider(
            new StubHttpClientFactory(new FakeHttpHandler(
                HttpStatusCode.Unauthorized,
                """{"Response":"False","Error":"Request limit reached!"}""")),
            gate,
            () => config);

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await provider.FetchAsync(Movie, MetadataCapability.Ratings, CancellationToken.None));
        }

        var health = gate.Snapshot(new[] { "omdb" }).Single();
        Assert.Equal(5, health.Failure);
        Assert.Equal(0, health.Empty);
        Assert.True(health.BreakerOpen);
    }

    [Fact]
    public async Task Omdb_ATitleItSimplyDoesNotHave_StaysASoftMiss()
    {
        // 404 must NOT trip the breaker — obscure titles are normal, not an outage.
        var config = new PluginConfiguration { OmdbApiKey = "key" };
        var gate = NewGate(config);
        var provider = new OmdbMetadataProvider(
            new StubHttpClientFactory(new FakeHttpHandler(HttpStatusCode.NotFound, "{}")), gate, () => config);

        for (var i = 0; i < 10; i++)
        {
            await provider.FetchAsync(Movie, MetadataCapability.Ratings, CancellationToken.None);
        }

        var health = gate.Snapshot(new[] { "omdb" }).Single();
        Assert.Equal(0, health.Failure);
        Assert.False(health.BreakerOpen);
    }

    [Fact]
    public async Task Omdb_AnUnreachableHost_FailsSoftAndIsCountedAsAFailure()
    {
        var config = new PluginConfiguration { OmdbApiKey = "key" };
        var gate = NewGate(config);
        var provider = new OmdbMetadataProvider(
            new StubHttpClientFactory(FakeHttpHandler.Throwing(new HttpRequestException("no route to host"))),
            gate,
            () => config);

        Assert.Null(await provider.FetchAsync(Movie, MetadataCapability.Ratings, CancellationToken.None));
        Assert.Equal(1, gate.Snapshot(new[] { "omdb" }).Single().Failure);
    }

    // --- Fanart -------------------------------------------------------------------------------

    [Fact]
    public void Fanart_PickImage_PrefersTheWantedLanguage_ThenLikes()
    {
        var images = new List<FanartMetadataProvider.FanartImage>
        {
            new() { Url = "de.png", Lang = "de", Likes = "99" },
            new() { Url = "en.png", Lang = "en", Likes = "3" },
            new() { Url = "textless.png", Lang = "00", Likes = "50" },
        };

        Assert.Equal("en.png", FanartMetadataProvider.PickImage(images, "en", 1000, 1500)!.Url);
    }

    [Fact]
    public void Fanart_PickImage_RanksTextlessAboveAWrongLanguage()
    {
        var images = new List<FanartMetadataProvider.FanartImage>
        {
            new() { Url = "de.png", Lang = "de", Likes = "99" },
            new() { Url = "textless.png", Lang = "00", Likes = "1" },
        };

        Assert.Equal("textless.png", FanartMetadataProvider.PickImage(images, "en", 1000, 1500)!.Url);
    }

    [Fact]
    public void Fanart_PickImage_UsesLikesWithinTheSameLanguage()
    {
        var images = new List<FanartMetadataProvider.FanartImage>
        {
            new() { Url = "few.png", Lang = "en", Likes = "2" },
            new() { Url = "many.png", Lang = "en", Likes = "40" },
        };

        Assert.Equal("many.png", FanartMetadataProvider.PickImage(images, "en", 1000, 1500)!.Url);
    }

    [Fact]
    public void Fanart_PickImage_HandlesAnEmptyList()
    {
        Assert.Null(FanartMetadataProvider.PickImage(null, "en", 1000, 1500));
        Assert.Null(FanartMetadataProvider.PickImage(new List<FanartMetadataProvider.FanartImage>(), "en", 1000, 1500));
    }

    [Fact]
    public void Fanart_Project_ReadsTheTvKeysForSeries_AndMovieKeysForFilms()
    {
        var response = new FanartMetadataProvider.FanartResponse
        {
            MoviePoster = new() { new() { Url = "movie-poster.png", Lang = "en", Likes = "5" } },
            TvPoster = new() { new() { Url = "tv-poster.png", Lang = "en", Likes = "5" } },
            HdTvLogo = new() { new() { Url = "tv-logo.png", Lang = "en", Likes = "5" } },
        };

        Assert.Equal("movie-poster.png", FanartMetadataProvider.Project(response, isSeries: false, "en")!.Poster!.Url);
        Assert.Equal("tv-poster.png", FanartMetadataProvider.Project(response, isSeries: true, "en")!.Poster!.Url);
        Assert.Equal("tv-logo.png", FanartMetadataProvider.Project(response, isSeries: true, "en")!.Logo!.Url);
    }

    [Fact]
    public void Fanart_Project_ReturnsNullWhenThereIsNoArtAtAll()
    {
        Assert.Null(FanartMetadataProvider.Project(new FanartMetadataProvider.FanartResponse(), isSeries: false, "en"));
        Assert.Null(FanartMetadataProvider.Project(null, isSeries: false, "en"));
    }

    [Fact]
    public async Task Fanart_IsNeverCalledForASeriesWithoutATvdbId()
    {
        // Fanart keys series by TVDB id, which is exactly why external-id resolution is load-bearing.
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var config = new PluginConfiguration { FanartApiKey = "key" };
        var provider = new FanartMetadataProvider(new StubHttpClientFactory(handler), NewGate(config), () => config);

        var noTvdb = Series with { TvdbId = null };
        Assert.Null(await provider.FetchAsync(noTvdb, MetadataCapability.Artwork, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fanart_AddressesMoviesByTmdbIdAndSeriesByTvdbId()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var config = new PluginConfiguration { FanartApiKey = "key" };
        var provider = new FanartMetadataProvider(new StubHttpClientFactory(handler), NewGate(config), () => config);

        await provider.FetchAsync(Movie, MetadataCapability.Artwork, CancellationToken.None);
        await provider.FetchAsync(Series, MetadataCapability.Artwork, CancellationToken.None);

        Assert.Contains("/v3/movies/157336", handler.Paths);
        Assert.Contains("/v3/tv/121361", handler.Paths);
    }

    [Fact]
    public async Task Fanart_ReadsLikesEvenThoughTheApiSendsThemAsStrings()
    {
        const string Body = """
            {"hdmovielogo":[{"url":"logo.png","lang":"en","likes":"12"}],
             "movieposter":[{"url":"poster.png","lang":"en","likes":"4"}]}
            """;
        var config = new PluginConfiguration { FanartApiKey = "key" };
        var provider = new FanartMetadataProvider(
            new StubHttpClientFactory(new FakeHttpHandler(HttpStatusCode.OK, Body)), NewGate(config), () => config);

        var fragment = await provider.FetchAsync(Movie, MetadataCapability.Logo, CancellationToken.None);

        Assert.Equal("logo.png", fragment!.Logo!.Url);
        Assert.Equal(12, fragment.Logo.Votes);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Fanart_FailsSoftOnBadResponses(HttpStatusCode status, string body)
    {
        var config = new PluginConfiguration { FanartApiKey = "key" };
        var provider = new FanartMetadataProvider(
            new StubHttpClientFactory(new FakeHttpHandler(status, body)), NewGate(config), () => config);

        Assert.Null(await provider.FetchAsync(Movie, MetadataCapability.Artwork, CancellationToken.None));
    }

    // --- TVDB ---------------------------------------------------------------------------------

    [Fact]
    public void Tvdb_PickArtwork_MatchesIso639_2LanguageCodes()
    {
        // TVDB reports "eng" where the rest of the engine says "en", so equality would never match.
        var artworks = new List<TvdbMetadataProvider.TvdbArtwork>
        {
            new() { Image = "de.png", Type = 2, Language = "deu", Score = 900 },
            new() { Image = "en.png", Type = 2, Language = "eng", Score = 100 },
        };

        Assert.Equal("en.png", TvdbMetadataProvider.PickArtwork(artworks, 2, "en")!.Url);
    }

    [Fact]
    public void Tvdb_PickArtwork_FiltersByTypeAndFallsBackToScore()
    {
        var artworks = new List<TvdbMetadataProvider.TvdbArtwork>
        {
            new() { Image = "background.png", Type = 3, Language = "eng", Score = 999 },
            new() { Image = "poster-low.png", Type = 2, Language = "fra", Score = 10 },
            new() { Image = "poster-high.png", Type = 2, Language = "fra", Score = 80 },
        };

        Assert.Equal("poster-high.png", TvdbMetadataProvider.PickArtwork(artworks, 2, "en")!.Url);
        Assert.Null(TvdbMetadataProvider.PickArtwork(artworks, 99, "en"));
    }

    [Fact]
    public void Tvdb_Project_FallsBackToThePrimaryImageWhenThereIsNoTypedArtwork()
    {
        var fragment = TvdbMetadataProvider.Project(
            new TvdbMetadataProvider.TvdbRecord { Image = "primary.png", Overview = "A show." },
            isSeries: true,
            "en");

        Assert.Equal("primary.png", fragment!.Poster!.Url);
        Assert.Equal("A show.", fragment.Overview);
    }

    [Fact]
    public void Tvdb_Project_PrefersATrailerInTheWantedLanguage()
    {
        var fragment = TvdbMetadataProvider.Project(
            new TvdbMetadataProvider.TvdbRecord
            {
                Overview = "x",
                Trailers = new()
                {
                    new() { Url = "https://youtu.be/de", Language = "deu" },
                    new() { Url = "https://youtu.be/en", Language = "en" },
                },
            },
            isSeries: true,
            "en");

        Assert.Equal("https://youtu.be/en", fragment!.TrailerUrl);
    }

    [Fact]
    public void Tvdb_Project_ReturnsNullForAnEmptyRecord()
    {
        Assert.Null(TvdbMetadataProvider.Project(new TvdbMetadataProvider.TvdbRecord(), isSeries: true, "en"));
        Assert.Null(TvdbMetadataProvider.Project(null, isSeries: true, "en"));
    }

    [Fact]
    public async Task Tvdb_IsNeverCalledWithoutATvdbId()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var config = new PluginConfiguration { TvdbApiKey = "key" };
        var provider = new TvdbMetadataProvider(
            new StubHttpClientFactory(handler), NewGate(config), new Wholphin.Engine.Caching.InMemoryCache(new RecordingMetrics()), () => config);

        Assert.Null(await provider.FetchAsync(Movie, MetadataCapability.Core, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Tvdb_LogsInOnce_ThenReusesTheCachedToken()
    {
        var handler = new FakeHttpHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":{"token":"secret-token"}}""")
                : FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":{"overview":"A show.","genres":[{"name":"Drama"}]}}"""));

        var config = new PluginConfiguration { TvdbApiKey = "key" };
        using var cache = new Wholphin.Engine.Caching.InMemoryCache(new RecordingMetrics());
        var provider = new TvdbMetadataProvider(new StubHttpClientFactory(handler), NewGate(config), cache, () => config);

        await provider.FetchAsync(Series, MetadataCapability.Core, CancellationToken.None);
        await provider.FetchAsync(Series, MetadataCapability.Core, CancellationToken.None);

        Assert.Equal(1, handler.Paths.Count(p => p.EndsWith("/login", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Tvdb_ReLogsInWhenTheCachedTokenIsRejected()
    {
        var seenSeriesRequests = 0;
        var handler = new FakeHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":{"token":"fresh"}}""");
            }

            // The first data call is rejected as if the cached token had expired.
            return ++seenSeriesRequests == 1
                ? FakeHttpHandler.Json(HttpStatusCode.Unauthorized, "{}")
                : FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":{"overview":"A show."}}""");
        });

        var config = new PluginConfiguration { TvdbApiKey = "key" };
        using var cache = new Wholphin.Engine.Caching.InMemoryCache(new RecordingMetrics());
        var provider = new TvdbMetadataProvider(new StubHttpClientFactory(handler), NewGate(config), cache, () => config);

        var fragment = await provider.FetchAsync(Series, MetadataCapability.Core, CancellationToken.None);

        Assert.Equal("A show.", fragment!.Overview);
        Assert.Equal(2, handler.Paths.Count(p => p.EndsWith("/login", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Tvdb_FailsSoftWhenLoginItselfFails()
    {
        var config = new PluginConfiguration { TvdbApiKey = "bad-key" };
        using var cache = new Wholphin.Engine.Caching.InMemoryCache(new RecordingMetrics());
        var provider = new TvdbMetadataProvider(
            new StubHttpClientFactory(new FakeHttpHandler(HttpStatusCode.Unauthorized, "{}")), NewGate(config), cache, () => config);

        Assert.Null(await provider.FetchAsync(Series, MetadataCapability.Core, CancellationToken.None));
    }
}
