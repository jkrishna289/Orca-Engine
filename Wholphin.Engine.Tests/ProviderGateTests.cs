using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Http;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The circuit breaker exists so one broken provider cannot cost every metadata request its full
/// timeout. The load-bearing distinction is what counts as a failure: a provider that simply has
/// nothing for a title is healthy, and tripping on that would disable OMDb over a few obscure films.
/// </summary>
public class ProviderGateTests
{
    private static (ProviderGate Gate, RecordingMetrics Metrics) Build(PluginConfiguration? config = null)
    {
        var metrics = new RecordingMetrics();
        var effective = config ?? new PluginConfiguration();
        return (new ProviderGate(metrics, NullLogger<ProviderGate>.Instance, () => effective), metrics);
    }

    private static Task<string?> Fail() => throw new HttpRequestException("upstream is down");

    [Fact]
    public async Task FiveConsecutiveFailures_OpenTheBreaker_AndTheOperationStopsBeingInvoked()
    {
        var (gate, metrics) = Build();
        var invocations = 0;

        Task<string?> Operation(CancellationToken _)
        {
            invocations++;
            return Fail();
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await gate.ExecuteAsync("omdb", Operation, CancellationToken.None));
        }

        Assert.Equal(5, invocations);

        // Open: the next call must be refused WITHOUT dialing. That is the whole point — a refused
        // call costs nothing, a dialed one costs the timeout.
        Assert.Null(await gate.ExecuteAsync("omdb", Operation, CancellationToken.None));
        Assert.Equal(5, invocations);

        Assert.Equal(1, metrics.Counters["provider.omdb.short"]);
        Assert.True(gate.Snapshot(Array.Empty<string>()).Single().BreakerOpen);
    }

    [Fact]
    public async Task FourFailures_DoNotOpenTheBreaker()
    {
        var (gate, _) = Build();
        for (var i = 0; i < 4; i++)
        {
            await gate.ExecuteAsync("omdb", _ => Fail(), CancellationToken.None);
        }

        var health = gate.Snapshot(Array.Empty<string>()).Single();
        Assert.False(health.BreakerOpen);
        Assert.Equal(4, health.ConsecutiveFailures);
    }

    [Fact]
    public async Task ASuccess_ResetsTheFailureStreak()
    {
        var (gate, _) = Build();
        for (var i = 0; i < 4; i++)
        {
            await gate.ExecuteAsync("tvdb", _ => Fail(), CancellationToken.None);
        }

        Assert.Equal("ok", await gate.ExecuteAsync("tvdb", _ => Task.FromResult<string?>("ok"), CancellationToken.None));

        var health = gate.Snapshot(Array.Empty<string>()).Single();
        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.Equal(1, health.Success);
        Assert.NotNull(health.LastSuccessUtc);
    }

    [Fact]
    public async Task AfterCooldown_ExactlyOneProbeIsAdmitted()
    {
        var config = new PluginConfiguration { MetadataProviderBreakerCooldownSeconds = 1 };
        var (gate, _) = Build(config);

        for (var i = 0; i < 5; i++)
        {
            await gate.ExecuteAsync("fanart", _ => Fail(), CancellationToken.None);
        }

        Assert.True(gate.Snapshot(Array.Empty<string>()).Single().BreakerOpen);
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        // Half-open: the probe runs and succeeds, which closes the breaker.
        Assert.Equal("back", await gate.ExecuteAsync("fanart", _ => Task.FromResult<string?>("back"), CancellationToken.None));
        Assert.False(gate.Snapshot(Array.Empty<string>()).Single().BreakerOpen);
    }

    [Fact]
    public async Task ANullResult_IsEmptyNotFailure_AndNeverTripsTheBreaker()
    {
        var (gate, metrics) = Build();
        var invocations = 0;

        for (var i = 0; i < 10; i++)
        {
            await gate.ExecuteAsync<string>(
                "omdb",
                _ =>
                {
                    invocations++;
                    return Task.FromResult<string?>(null);
                },
                CancellationToken.None);
        }

        Assert.Equal(10, invocations);
        var health = gate.Snapshot(Array.Empty<string>()).Single();
        Assert.False(health.BreakerOpen);
        Assert.Equal(0, health.Failure);
        Assert.Equal(10, health.Empty);
        Assert.Equal(10, metrics.Counters["provider.omdb.empty"]);
    }

    [Fact]
    public async Task RateLimiting_IsCountedSeparately_AndIsNotAFailure()
    {
        var (gate, metrics) = Build();

        for (var i = 0; i < 10; i++)
        {
            Assert.Null(await gate.ExecuteAsync<string>(
                "omdb",
                _ => throw new ProviderRateLimitedException(TimeSpan.FromSeconds(3)),
                CancellationToken.None));
        }

        var health = gate.Snapshot(Array.Empty<string>()).Single();
        Assert.Equal(10, health.RateLimited);
        Assert.Equal(0, health.Failure);
        Assert.False(health.BreakerOpen);
        Assert.Equal(10, metrics.Counters["provider.omdb.ratelimited"]);
    }

    [Fact]
    public async Task ATimeout_IsCountedSeparatelyFromAnError_AndStillFailsSoft()
    {
        var config = new PluginConfiguration { MetadataProviderTimeoutSeconds = 1 };
        var (gate, metrics) = Build(config);

        var result = await gate.ExecuteAsync<string>(
            "tvdb",
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return "never";
            },
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, metrics.Counters["provider.tvdb.timeout"]);
        Assert.Equal(1, gate.Snapshot(Array.Empty<string>()).Single().Timeout);
    }

    [Fact]
    public async Task CallerCancellation_IsRethrown_NotSwallowedAsAProviderFault()
    {
        var (gate, _) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.ExecuteAsync<string>("tmdb", ct => Task.FromException<string?>(new OperationCanceledException(ct)), cts.Token));
    }

    [Fact]
    public async Task ConcurrencyNeverExceedsTheConfiguredCap()
    {
        var config = new PluginConfiguration { MetadataProviderMaxConcurrency = 2 };
        var (gate, _) = Build(config);

        var current = 0;
        var peak = 0;

        var calls = Enumerable.Range(0, 12).Select(_ => gate.ExecuteAsync<string>(
            "fanart",
            async ct =>
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref peak, now);
                await Task.Delay(20, ct);
                Interlocked.Decrement(ref current);
                return "ok";
            },
            CancellationToken.None));

        await Task.WhenAll(calls);
        Assert.True(peak <= 2, $"peak concurrency was {peak}, expected at most 2");
    }

    [Fact]
    public async Task Snapshot_ReportsConfiguredWithoutEverCarryingTheKey()
    {
        var (gate, _) = Build();
        await gate.ExecuteAsync("omdb", _ => Task.FromResult<string?>("x"), CancellationToken.None);

        var health = gate.Snapshot(new[] { "omdb" }).Single();
        Assert.True(health.Configured);
        Assert.Null(health.LastFailureKind);
    }

    [Fact]
    public async Task AFailure_RecordsOnlyTheExceptionTypeName_NeverItsMessage()
    {
        // Provider URLs carry the API key in the query string, and HttpRequestException.Message can
        // contain the URL. The diagnostics endpoint is served over HTTP.
        var (gate, metrics) = Build();
        await gate.ExecuteAsync<string>(
            "omdb",
            _ => throw new HttpRequestException("GET https://www.omdbapi.com/?apikey=SUPERSECRET failed"),
            CancellationToken.None);

        var recorded = string.Join("\n", metrics.Records.Select(r => $"{r.Key} {r.Data}"));
        Assert.DoesNotContain("SUPERSECRET", recorded, StringComparison.Ordinal);
        Assert.Contains(nameof(HttpRequestException), recorded, StringComparison.Ordinal);
        Assert.Equal(nameof(HttpRequestException), gate.Snapshot(Array.Empty<string>()).Single().LastFailureKind);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
            {
                return;
            }
        }
    }
}
