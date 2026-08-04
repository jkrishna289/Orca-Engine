using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Home;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The home is built row-by-row concurrently, and <see cref="RowBudget"/> is what guarantees a slow
/// or broken row degrades to a missing row instead of a stalled or failed page — the whole reason
/// the app stopped timing out and falling back to the legacy home. These lock that in.
/// </summary>
public class RowBudgetTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task OrDefault_ReturnsTheRow_WhenTheProducerFinishesInTime()
    {
        var metrics = new CountingMetrics();

        var row = await RowBudget.OrDefaultAsync<string>(metrics, "fast", Budget, _ => Task.FromResult<string?>("row"), CancellationToken.None);

        Assert.Equal("row", row);

        // A healthy row is TIMED but not flagged: the count/total_ms pair is what
        // Admin/Diagnostics averages, and a clean run must never trip timeout/error.
        Assert.Equal(1, metrics.Counters["home.row.fast.count"]);
        Assert.True(metrics.Counters.ContainsKey("home.row.fast.total_ms"));
        Assert.False(metrics.Counters.ContainsKey("home.row.fast.timeout"));
        Assert.False(metrics.Counters.ContainsKey("home.row.fast.error"));
    }

    [Fact]
    public async Task OrDefault_DropsTheRow_WhenTheProducerBlowsItsBudget()
    {
        var metrics = new CountingMetrics();

        // A row that never finishes must not hold the page: it's abandoned at the budget.
        var row = await RowBudget.OrDefaultAsync<string>(metrics, "slow", Budget, async c =>
        {
            await Task.Delay(Timeout.Infinite, c);
            return "never";
        },
        CancellationToken.None);

        Assert.Null(row);
        Assert.Equal(1, metrics.Counters["home.row.slow.timeout"]);

        // Timed even though it failed — a row that always burns its whole budget before giving up
        // is precisely the one the timing is meant to surface.
        Assert.Equal(1, metrics.Counters["home.row.slow.count"]);
        Assert.True(metrics.Counters.ContainsKey("home.row.slow.total_ms"));
    }

    [Fact]
    public async Task OrDefault_DropsTheRow_WhenTheProducerThrows()
    {
        var metrics = new CountingMetrics();

        var row = await RowBudget.OrDefaultAsync<string>(metrics, "broken", Budget, _ => throw new InvalidOperationException("boom"), CancellationToken.None);

        Assert.Null(row);
        Assert.Equal(1, metrics.Counters["home.row.broken.error"]);
    }

    [Fact]
    public async Task OrDefault_Propagates_WhenTheCallerCancels()
    {
        var metrics = new CountingMetrics();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        // The client gave up on the whole page — that is NOT a row to swallow.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RowBudget.OrDefaultAsync<string>(metrics, "abandoned", Budget, async c =>
            {
                await Task.Delay(Timeout.Infinite, c);
                return "never";
            },
            caller.Token));

        Assert.Empty(metrics.Counters);
    }

    [Fact]
    public async Task OrFallback_KeepsTheLocalOrdering_WhenTheStepIsTooSlow()
    {
        var metrics = new CountingMetrics();

        var result = await RowBudget.OrFallbackAsync(metrics, "llm", Budget, async c =>
        {
            await Task.Delay(Timeout.Infinite, c);
            return "curated";
        },
        fallback: "local",
        ct: CancellationToken.None);

        Assert.Equal("local", result);
        Assert.Equal(1, metrics.Counters["home.llm.timeout"]);
    }

    /// <summary>Minimal metrics sink: just enough to assert which counter fired.</summary>
    private sealed class CountingMetrics : IEngineMetrics
    {
        public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the (key, ok) pair of every timed operation, in order.</summary>
        public List<(string Key, bool Ok)> Recorded { get; } = new();

        public void Increment(string key, long by = 1) =>
            Counters[key] = Counters.TryGetValue(key, out var current) ? current + by : by;

        public void Record(string key, long elapsedMs, bool ok = true, string? data = null, Exception? exception = null)
        {
            Recorded.Add((key, ok));
            Increment($"{key}.count");
            Increment($"{key}.total_ms", elapsedMs);
        }

        public IReadOnlyDictionary<string, long> Snapshot() => Counters;

        public void Reset() => Counters.Clear();
    }
}
