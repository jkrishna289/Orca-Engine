using Wholphin.Engine.Caching;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Cache-or-compute with in-flight coalescing. The load-bearing test is the concurrent one: ten
/// clients opening the same title must produce ONE upstream call, not ten.
/// </summary>
public class SingleFlightTests
{
    private static readonly TimeSpan Positive = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Negative = TimeSpan.FromMinutes(1);

    private static InMemoryCache NewCache() => new(new RecordingMetrics());

    [Fact]
    public async Task TenConcurrentCallers_ProduceExactlyOneFactoryRun()
    {
        using var cache = NewCache();
        var runs = 0;
        using var release = new SemaphoreSlim(0);

        async Task<string?> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            await release.WaitAsync(ct);
            return "value";
        }

        var callers = Enumerable.Range(0, 10)
            .Select(_ => SingleFlight.GetOrCreateAsync(cache, "meta:tmdb:1", Positive, Negative, Factory, CancellationToken.None))
            .ToArray();

        // Everyone is now either awaiting the one factory or about to. Let it finish.
        release.Release();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, runs);
        Assert.All(results, r => Assert.Equal("value", r));
    }

    [Fact]
    public async Task ASecondCall_IsServedFromCache()
    {
        using var cache = NewCache();
        var runs = 0;

        Task<string?> Factory(CancellationToken _)
        {
            runs++;
            return Task.FromResult<string?>("hit");
        }

        Assert.Equal("hit", await SingleFlight.GetOrCreateAsync(cache, "meta:a", Positive, Negative, Factory, CancellationToken.None));
        Assert.Equal("hit", await SingleFlight.GetOrCreateAsync(cache, "meta:a", Positive, Negative, Factory, CancellationToken.None));

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ANullResult_IsCachedTooSoAKnownMissIsNotRefetched()
    {
        using var cache = NewCache();
        var runs = 0;

        Task<string?> Factory(CancellationToken _)
        {
            runs++;
            return Task.FromResult<string?>(null);
        }

        Assert.Null(await SingleFlight.GetOrCreateAsync(cache, "meta:missing", Positive, Negative, Factory, CancellationToken.None));
        Assert.Null(await SingleFlight.GetOrCreateAsync(cache, "meta:missing", Positive, Negative, Factory, CancellationToken.None));

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ANegativeResult_ExpiresOnItsOwnShorterTtl()
    {
        using var cache = NewCache();
        var runs = 0;

        Task<string?> Factory(CancellationToken _)
        {
            runs++;
            return Task.FromResult<string?>(null);
        }

        var negative = TimeSpan.FromMilliseconds(150);
        await SingleFlight.GetOrCreateAsync(cache, "meta:expiring", Positive, negative, Factory, CancellationToken.None);
        await Task.Delay(400);
        await SingleFlight.GetOrCreateAsync(cache, "meta:expiring", Positive, negative, Factory, CancellationToken.None);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task AThrowingFactory_DoesNotPoisonTheKey_AndIsNotCachedAsAMiss()
    {
        // A transient failure is not evidence of absence. Caching it as one would hide the provider
        // for the whole negative TTL after a single blip.
        using var cache = NewCache();
        var runs = 0;

        Task<string?> Factory(CancellationToken _)
        {
            runs++;
            return runs == 1
                ? Task.FromException<string?>(new HttpRequestException("blip"))
                : Task.FromResult<string?>("recovered");
        }

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            SingleFlight.GetOrCreateAsync(cache, "meta:blip", Positive, Negative, Factory, CancellationToken.None));

        Assert.Equal("recovered", await SingleFlight.GetOrCreateAsync(cache, "meta:blip", Positive, Negative, Factory, CancellationToken.None));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task DifferentKeys_DoNotCoalesceWithEachOther()
    {
        using var cache = NewCache();
        var runs = 0;

        Task<string?> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult<string?>("v");
        }

        await SingleFlight.GetOrCreateAsync(cache, "meta:tmdb:1", Positive, Negative, Factory, CancellationToken.None);
        await SingleFlight.GetOrCreateAsync(cache, "meta:tmdb:2", Positive, Negative, Factory, CancellationToken.None);

        Assert.Equal(2, runs);
    }
}
