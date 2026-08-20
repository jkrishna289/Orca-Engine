using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Http;
using Wholphin.Engine.Metadata;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The orchestration rule that keeps this from becoming "call every provider every time": a provider
/// is asked only when it is configured, only when it declares a field that is actually missing, and
/// only until nothing is missing.
/// </summary>
public class MetadataAggregatorTests
{
    private static readonly MediaIdentity Identity =
        new(157336, "tt0816692", 4242, null, MediaType.Movie, "Interstellar", null, 2014);

    /// <summary>A provider that records every call made to it.</summary>
    private sealed class SpyProvider : IMetadataProvider
    {
        private readonly MetadataFragment? _fragment;
        private readonly List<string>? _log;

        public SpyProvider(string name, MetadataCapability capabilities, MetadataFragment? fragment, bool configured = true, List<string>? log = null)
        {
            Name = name;
            Capabilities = capabilities;
            IsConfigured = configured;
            _fragment = fragment;
            _log = log;
        }

        public string Name { get; }

        public MetadataCapability Capabilities { get; }

        public bool IsConfigured { get; }

        public int Calls { get; private set; }

        public MetadataCapability LastWanted { get; private set; }

        public Task<MetadataFragment?> FetchAsync(MediaIdentity identity, MetadataCapability wanted, CancellationToken cancellationToken)
        {
            Calls++;
            LastWanted = wanted;
            _log?.Add(Name);
            return Task.FromResult(_fragment);
        }
    }

    private static (MetadataAggregator Aggregator, InMemoryCache Cache) Build(
        PluginConfiguration config,
        params IMetadataProvider[] providers)
    {
        var metrics = new RecordingMetrics();
        var cache = new InMemoryCache(metrics);
        var gate = new ProviderGate(metrics, NullLogger<ProviderGate>.Instance, () => config);
        return (new MetadataAggregator(providers, gate, cache, metrics, NullLogger<MetadataAggregator>.Instance, () => config), cache);
    }

    [Fact]
    public async Task OnlyProvidersCapableOfTheMissingFieldsAreAsked()
    {
        var ratingsOnly = new SpyProvider("omdb", MetadataCapability.Ratings, new MetadataFragment
        {
            Source = "omdb",
            Ratings = new Dictionary<string, double> { ["rt"] = 73 },
        });
        var artworkOnly = new SpyProvider("fanart", MetadataCapability.Artwork | MetadataCapability.Logo, null);

        var (aggregator, cache) = Build(new PluginConfiguration(), ratingsOnly, artworkOnly);
        using var _ = cache;

        await aggregator.FetchAsync(Identity, MetadataCapability.Ratings, CancellationToken.None);

        Assert.Equal(1, ratingsOnly.Calls);
        Assert.Equal(0, artworkOnly.Calls);
    }

    [Fact]
    public async Task UnconfiguredProvidersAreNeverAsked()
    {
        var unconfigured = new SpyProvider("omdb", MetadataCapability.Ratings, null, configured: false);

        var (aggregator, cache) = Build(new PluginConfiguration(), unconfigured);
        using var _ = cache;

        Assert.Null(await aggregator.FetchAsync(Identity, MetadataCapability.Ratings, CancellationToken.None));
        Assert.Equal(0, unconfigured.Calls);
    }

    [Fact]
    public async Task ProvidersAreAskedInTheConfiguredPriorityOrder()
    {
        var order = new List<string>();

        // Registered tmdb-first on purpose: the CONFIG must decide the order, not the DI sequence.
        var tmdb = new SpyProvider("tmdb", MetadataCapability.Artwork, null, log: order);
        var fanart = new SpyProvider("fanart", MetadataCapability.Artwork, null, log: order);

        var config = new PluginConfiguration { MetadataPriorityArtwork = "fanart,tmdb" };
        var (aggregator, cache) = Build(config, tmdb, fanart);
        using var _ = cache;

        await aggregator.FetchAsync(Identity, MetadataCapability.Artwork, CancellationToken.None);

        Assert.Equal(new[] { "fanart", "tmdb" }, order);
    }

    [Fact]
    public async Task ReorderingThePriorityReordersTheCalls()
    {
        var order = new List<string>();
        var tmdb = new SpyProvider("tmdb", MetadataCapability.Artwork, null, log: order);
        var fanart = new SpyProvider("fanart", MetadataCapability.Artwork, null, log: order);

        var config = new PluginConfiguration { MetadataPriorityArtwork = "tmdb,fanart" };
        var (aggregator, cache) = Build(config, tmdb, fanart);
        using var _ = cache;

        await aggregator.FetchAsync(Identity, MetadataCapability.Artwork, CancellationToken.None);

        Assert.Equal(new[] { "tmdb", "fanart" }, order);
    }

    [Fact]
    public async Task OnceNothingIsMissing_TheRemainingProvidersAreSkipped()
    {
        var complete = new SpyProvider("fanart", MetadataCapability.Artwork | MetadataCapability.Logo, new MetadataFragment
        {
            Source = "fanart",
            Poster = new ImageCandidate("p.png", 1000, 1500, "en", 5),
            Backdrop = new ImageCandidate("b.png", 1920, 1080, "en", 5),
            Logo = new ImageCandidate("l.png", 800, 310, "en", 5),
        });
        var second = new SpyProvider("tvdb", MetadataCapability.Artwork | MetadataCapability.Logo, null);

        var config = new PluginConfiguration
        {
            MetadataPriorityArtwork = "fanart,tvdb",
            MetadataPriorityLogo = "fanart,tvdb",
        };
        var (aggregator, cache) = Build(config, complete, second);
        using var _ = cache;

        await aggregator.FetchAsync(Identity, MetadataCapability.Artwork | MetadataCapability.Logo, CancellationToken.None);

        Assert.Equal(1, complete.Calls);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task AProviderIsToldOnlyWhatIsStillMissing()
    {
        var first = new SpyProvider("fanart", MetadataCapability.Logo, new MetadataFragment
        {
            Source = "fanart",
            Logo = new ImageCandidate("l.png", 800, 310, "en", 5),
        });
        var second = new SpyProvider("tvdb", MetadataCapability.Core | MetadataCapability.Logo, null);

        var config = new PluginConfiguration { MetadataPriorityLogo = "fanart,tvdb", MetadataPriorityCore = "tvdb" };
        var (aggregator, cache) = Build(config, first, second);
        using var _ = cache;

        await aggregator.FetchAsync(Identity, MetadataCapability.Core | MetadataCapability.Logo, CancellationToken.None);

        Assert.Equal(1, second.Calls);
        Assert.Equal(MetadataCapability.None, second.LastWanted & MetadataCapability.Logo);
        Assert.Equal(MetadataCapability.Core, second.LastWanted & MetadataCapability.Core);
    }

    [Fact]
    public async Task AProviderReturningNothing_DoesNotBreakTheMerge()
    {
        var silent = new SpyProvider("tvdb", MetadataCapability.Core, null);
        var answering = new SpyProvider("tmdb", MetadataCapability.Core, new MetadataFragment
        {
            Source = "tmdb",
            Overview = "Present.",
        });

        var config = new PluginConfiguration { MetadataPriorityCore = "tvdb,tmdb" };
        var (aggregator, cache) = Build(config, silent, answering);
        using var _ = cache;

        var merged = await aggregator.FetchAsync(Identity, MetadataCapability.Core, CancellationToken.None);

        Assert.Equal("Present.", merged!.Overview);
    }

    [Fact]
    public async Task ATitleWithNoExternalIdAtAll_IsNeverLookedUp()
    {
        var provider = new SpyProvider("omdb", MetadataCapability.Ratings, null);
        var (aggregator, cache) = Build(new PluginConfiguration(), provider);
        using var _ = cache;

        var anonymous = new MediaIdentity(null, null, null, null, MediaType.Movie, "Unmatched", null, null);

        Assert.Null(await aggregator.FetchAsync(anonymous, MetadataCapability.Ratings, CancellationToken.None));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ASecondLookupForTheSameTitle_IsServedFromCache()
    {
        var provider = new SpyProvider("omdb", MetadataCapability.Ratings, new MetadataFragment
        {
            Source = "omdb",
            Ratings = new Dictionary<string, double> { ["rt"] = 73 },
        });

        var (aggregator, cache) = Build(new PluginConfiguration(), provider);
        using var _ = cache;

        await aggregator.FetchAsync(Identity, MetadataCapability.Ratings, CancellationToken.None);
        await aggregator.FetchAsync(Identity, MetadataCapability.Ratings, CancellationToken.None);

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task TenSimultaneousLookups_CauseExactlyOneProviderCall()
    {
        // Ten clients opening the same title must not become ten OMDb requests against a 1000/day tier.
        var calls = 0;
        using var release = new SemaphoreSlim(0);

        var provider = new SlowProvider(
            async ct =>
            {
                Interlocked.Increment(ref calls);
                await release.WaitAsync(ct);
                return new MetadataFragment { Source = "omdb", Ratings = new Dictionary<string, double> { ["rt"] = 73 } };
            });

        var (aggregator, cache) = Build(new PluginConfiguration(), provider);
        using var _ = cache;

        var lookups = Enumerable.Range(0, 10)
            .Select(_ => aggregator.FetchAsync(Identity, MetadataCapability.Ratings, CancellationToken.None))
            .ToArray();

        release.Release();
        var results = await Task.WhenAll(lookups);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal(73, r!.Ratings["rt"]));
    }

    [Fact]
    public void ConfiguredProviders_ListsOnlyThoseHoldingAKey()
    {
        var (aggregator, cache) = Build(
            new PluginConfiguration(),
            new SpyProvider("omdb", MetadataCapability.Ratings, null),
            new SpyProvider("fanart", MetadataCapability.Artwork, null, configured: false));
        using var _ = cache;

        Assert.Equal(new[] { "omdb" }, aggregator.ConfiguredProviders());
    }

    [Theory]
    [InlineData(MetadataCapability.None)]
    public async Task AskingForNothing_CallsNobody(MetadataCapability wanted)
    {
        var provider = new SpyProvider("omdb", MetadataCapability.Ratings, null);
        var (aggregator, cache) = Build(new PluginConfiguration(), provider);
        using var _ = cache;

        Assert.Null(await aggregator.FetchAsync(Identity, wanted, CancellationToken.None));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void Satisfied_OnlyCountsArtworkWhenBothImagesArePresent()
    {
        var posterOnly = new MetadataFragment { Poster = new ImageCandidate("p", 1, 1, null, 0) };
        var both = new MetadataFragment
        {
            Poster = new ImageCandidate("p", 1, 1, null, 0),
            Backdrop = new ImageCandidate("b", 1, 1, null, 0),
        };

        Assert.Equal(MetadataCapability.None, MetadataAggregator.Satisfied(posterOnly) & MetadataCapability.Artwork);
        Assert.Equal(MetadataCapability.Artwork, MetadataAggregator.Satisfied(both) & MetadataCapability.Artwork);
    }

    /// <summary>A provider whose fetch is driven by a supplied delegate, for the concurrency test.</summary>
    private sealed class SlowProvider : IMetadataProvider
    {
        private readonly Func<CancellationToken, Task<MetadataFragment?>> _fetch;

        public SlowProvider(Func<CancellationToken, Task<MetadataFragment?>> fetch) => _fetch = fetch;

        public string Name => "omdb";

        public MetadataCapability Capabilities => MetadataCapability.Ratings;

        public bool IsConfigured => true;

        public Task<MetadataFragment?> FetchAsync(MediaIdentity identity, MetadataCapability wanted, CancellationToken cancellationToken)
            => _fetch(cancellationToken);
    }
}
