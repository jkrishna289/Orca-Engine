using System.Globalization;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Embedding;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Bounded-batch embedding: how a catalog-sized corpus is split, how a failing batch is handled,
/// and — the one that silently corrupts everything if it is wrong — that every returned vector stays
/// paired with the document that produced it.
/// </summary>
public class EmbeddingBatchingTests
{
    // ---- batch sizing -----------------------------------------------------------------------

    [Theory]
    [InlineData(null, 96)]     // default
    [InlineData(0, 96)]        // unset/zero falls back to the default
    [InlineData(-5, 96)]       // nonsense falls back to the default
    [InlineData(1, 8)]         // clamped up to the minimum
    [InlineData(100_000, 512)] // clamped down — cannot recreate the whole-catalog request
    [InlineData(64, 64)]       // honoured
    public void ConfiguredBatchSizeIsClampedToSafeBounds(int? configured, int expected)
    {
        var provider = new FakeProvider("cloud") { MaxBatchSize = 4096 };

        Assert.Equal(expected, EmbeddingService.ResolveBatchSize(provider, configured));
    }

    [Fact]
    public void AProviderWithATighterCeilingWinsOverConfiguration()
    {
        var provider = new FakeProvider("local") { MaxBatchSize = 32 };

        Assert.Equal(32, EmbeddingService.ResolveBatchSize(provider, 512));
    }

    // ---- batching ---------------------------------------------------------------------------

    [Fact]
    public async Task ACorpusSmallerThanOneBatchIsASingleCall()
    {
        var provider = new FakeProvider("cloud");
        var run = await Service(provider, batchSize: 10).EmbedCorpusAsync(Docs(7));

        Assert.True(run.Succeeded);
        Assert.Equal(new[] { 7 }, provider.BatchSizes);
        Assert.Equal(1, run.BatchesCompleted);
    }

    [Fact]
    public async Task ACorpusExactlyOneBatchIsASingleCall()
    {
        var provider = new FakeProvider("cloud");
        var run = await Service(provider, batchSize: 10).EmbedCorpusAsync(Docs(10));

        Assert.True(run.Succeeded);
        Assert.Equal(new[] { 10 }, provider.BatchSizes);
        Assert.Equal(1, run.BatchesCompleted);
    }

    [Fact]
    public async Task ALargeCorpusIsSplitAndNoBatchExceedsTheLimit()
    {
        var provider = new FakeProvider("cloud");
        var run = await Service(provider, batchSize: 10).EmbedCorpusAsync(Docs(95));

        Assert.True(run.Succeeded);
        Assert.Equal(10, provider.BatchSizes.Count);
        Assert.All(provider.BatchSizes, size => Assert.True(size <= 10, $"batch of {size} exceeded the limit"));
        Assert.Equal(95, provider.BatchSizes.Sum());
        Assert.Equal(10, run.BatchesCompleted);
    }

    [Fact]
    public async Task AnEmptyCorpusIsNotAFailure()
    {
        var provider = new FakeProvider("cloud");
        var run = await Service(provider, batchSize: 10).EmbedCorpusAsync(Array.Empty<string>());

        Assert.Empty(run.Vectors);
        Assert.Null(run.Failure);
        Assert.Equal(0, provider.Calls);
    }

    // ---- ordering ---------------------------------------------------------------------------

    [Fact]
    public async Task EveryVectorStaysPairedWithItsOwnDocumentAcrossBatchBoundaries()
    {
        var provider = new FakeProvider("cloud");
        var run = await Service(provider, batchSize: 10).EmbedCorpusAsync(Docs(95));

        Assert.True(run.Succeeded);
        Assert.Equal(95, run.Vectors.Count);

        // The failure this guards against is silent: a shifted result set still produces a full,
        // plausible-looking index in which every title is described by a different title's text.
        for (var i = 0; i < 95; i++)
        {
            Assert.Equal(i, FakeProvider.Decode(run.Vectors[i]));
        }
    }

    [Fact]
    public async Task TheOpenAiShapedProviderReordersAnOutOfOrderResponse()
    {
        // OpenAI documents data[] as unordered and carries the position in data[].index. A provider
        // that appended results in arrival order would hand back a plausible, silently wrong index.
        const string body = """
        {"data":[
          {"index":2,"embedding":[2.0,1.0]},
          {"index":0,"embedding":[0.0,1.0]},
          {"index":1,"embedding":[1.0,1.0]}
        ]}
        """;

        var handler = new FakeHttpHandler(HttpStatusCode.OK, body);
        var provider = new TestOpenAiProvider(new StubHttpClientFactory(handler), new RecordingMetrics());

        var vectors = await provider.EmbedAsync(new[] { "doc-0", "doc-1", "doc-2" });

        Assert.NotNull(vectors);
        Assert.Equal(3, vectors!.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i, FakeProvider.Decode(vectors[i]));
        }
    }

    // ---- failure ----------------------------------------------------------------------------

    [Fact]
    public async Task AFailedBatchFailsTheRunRatherThanSubstitutingAnotherModel()
    {
        var cloud = new FakeProvider("cloud") { FailAtOffset = { 20 } };
        var service = Service(cloud, batchSize: 10);

        var run = await service.EmbedCorpusAsync(Docs(95));

        // No provider can stand in for another: a second model's vectors have different dimensions,
        // so a patched index would score the substituted items at cosine 0 against everything else.
        Assert.False(run.Succeeded);
        Assert.Empty(run.Vectors);
        Assert.Equal(2, run.BatchesCompleted);
        Assert.Equal(1, run.BatchesFailed);
        Assert.NotNull(run.Failure);
    }

    [Fact]
    public async Task APermanentFailureRaisesACriticalAlertThatStaysUp()
    {
        var cloud = new FakeProvider("cloud") { FailAtOffset = { 0 } };
        var alerts = new RecordingAlerts();

        await Service(cloud, batchSize: 10, alerts: alerts).EmbedCorpusAsync(Docs(30));

        var alert = Assert.Single(alerts.Active());
        Assert.Equal(EmbeddingService.FallbackAlertKey, alert.Key);
        Assert.Equal("critical", alert.Level);
    }

    [Fact]
    public async Task ASuccessfulRunClearsAStandingAlert()
    {
        var alerts = new RecordingAlerts();
        alerts.Raise(EmbeddingService.FallbackAlertKey, "critical", "stale");

        await Service(new FakeProvider("cloud"), batchSize: 10, alerts: alerts).EmbedCorpusAsync(Docs(30));

        Assert.Empty(alerts.Active());
    }

    [Fact]
    public async Task ATransientFailureRecoversWithinTheRetryBudget()
    {
        var provider = new FakeProvider("cloud") { FailFirstAttempts = 2 };
        var run = await Service(provider, batchSize: 10).EmbedCorpusAsync(Docs(10));

        Assert.True(run.Succeeded);
        Assert.Equal(EmbeddingService.MaxAttemptsPerBatch, provider.Calls);
    }

    [Fact]
    public async Task RetriesAreBoundedAndExhaustionIsVisible()
    {
        var cloud = new FakeProvider("cloud") { FailFirstAttempts = int.MaxValue };

        var run = await Service(cloud, batchSize: 10).EmbedCorpusAsync(Docs(10));

        Assert.Equal(EmbeddingService.MaxAttemptsPerBatch, cloud.Calls);
        Assert.False(run.Succeeded);
        Assert.Equal(1, run.BatchesFailed);
        Assert.NotNull(run.Failure);
    }

    [Fact]
    public async Task AProviderReturningTheWrongCountIsRejectedRatherThanTrustedPartially()
    {
        var cloud = new FakeProvider("cloud") { ShortResultAtOffset = 10 };

        var run = await Service(cloud, batchSize: 10).EmbedCorpusAsync(Docs(50));

        Assert.False(run.Succeeded);
        Assert.NotNull(run.Failure);
        Assert.Contains("returned", run.Failure!, StringComparison.Ordinal);
        Assert.Empty(run.Vectors);
    }

    [Fact]
    public async Task AnUnconfiguredProviderIsNeverCalled()
    {
        var cloud = new FakeProvider("cloud") { IsConfigured = false };

        var run = await Service(cloud, batchSize: 10).EmbedCorpusAsync(Docs(20));

        // Retrying a missing server URL would just be a slower way to fail.
        Assert.False(run.Succeeded);
        Assert.Equal(0, cloud.Calls);
        Assert.NotNull(run.Failure);
    }

    // ---- cancellation -----------------------------------------------------------------------

    [Fact]
    public async Task CancellationBetweenBatchesPropagatesInsteadOfFailingTheProvider()
    {
        using var cts = new CancellationTokenSource();
        var cloud = new FakeProvider("cloud");
        var alerts = new RecordingAlerts();
        var service = Service(cloud, batchSize: 10, alerts: alerts);

        // Progress fires only once a batch has been accepted in full, so cancelling here lands
        // squarely between two batches rather than inside one.
        var progress = new ImmediateProgress<EmbeddingProgress>(p =>
        {
            if (p.BatchesCompleted == 2)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EmbedCorpusAsync(Docs(95), progress, cts.Token));

        // A cancelled rebuild is not a broken provider, and must not be recorded as one.
        Assert.Empty(alerts.Active());
        Assert.Equal(2, cloud.BatchSizes.Count);
    }

    [Fact]
    public async Task CancellationDuringABatchPropagates()
    {
        using var cts = new CancellationTokenSource();
        var cloud = new FakeProvider("cloud") { CancelDuringBatch = cts };
        var service = Service(cloud, batchSize: 10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EmbedCorpusAsync(Docs(50), null, cts.Token));
    }

    // ---- existing providers still behave ------------------------------------------------------

    [Fact]
    public void TheRealProvidersDeclareABoundedBatch()
    {
        var onnx = new OnnxEmbeddingProvider(NullLogger<OnnxEmbeddingProvider>.Instance);
        var ollama = new OllamaEmbeddingProvider(
            new StubHttpClientFactory(new FakeHttpHandler(HttpStatusCode.OK, "{}")),
            new RecordingMetrics(),
            NullLogger<OllamaEmbeddingProvider>.Instance);

        Assert.InRange(onnx.MaxBatchSize, EmbeddingService.MinBatchSize, EmbeddingService.MaxBatchSize);
        Assert.InRange(ollama.MaxBatchSize, EmbeddingService.MinBatchSize, EmbeddingService.MaxBatchSize);
    }

    [Fact]
    public async Task SelectingAProviderThatIsNotConfiguredIsCaughtBeforeAnyCall()
    {
        var onnx = new OnnxEmbeddingProvider(NullLogger<OnnxEmbeddingProvider>.Instance);
        var alerts = new RecordingAlerts();

        var run = await Service(onnx, batchSize: 16, alerts: alerts).EmbedCorpusAsync(Docs(40));

        // The engine never called it at all. That is precisely the state an operator cannot
        // otherwise see, so it must show up as a standing alert.
        Assert.False(run.Succeeded);
        Assert.NotNull(run.Failure);

        var alert = Assert.Single(alerts.Active());
        Assert.Equal("critical", alert.Level);
        Assert.Contains("not configured", alert.Title, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string[] Docs(int count) =>
        Enumerable.Range(0, count).Select(i => $"doc-{i}").ToArray();

    /// <summary>
    /// Builds a service whose ACTIVE provider is <paramref name="active"/>, selected by
    /// configuration exactly as it is in production — no test-only resolution path.
    /// </summary>
    private static EmbeddingService Service(
        IEmbeddingProvider active,
        int batchSize,
        RecordingAlerts? alerts = null)
    {
        var providers = new List<IEmbeddingProvider> { active };

        var config = new PluginConfiguration
        {
            EmbeddingProvider = active.Name,
            EmbeddingBatchSize = batchSize,
        };

        return new EmbeddingService(
            providers,
            alerts ?? new RecordingAlerts(),
            new RecordingMetrics(),
            NullLogger<EmbeddingService>.Instance,
            () => config,
            TimeSpan.Zero);
    }
}

/// <summary>
/// A scriptable <see cref="IEmbeddingProvider"/> that records exactly how it was called.
/// </summary>
/// <remarks>
/// Vectors encode their document's ordinal as <c>[n, 1]</c>, which survives the L2 normalization
/// <see cref="ContentVector.Dense"/> applies — so a test can decode any vector and prove which
/// document produced it. A single-component vector would normalize to <c>[1]</c> and lose the value.
/// </remarks>
internal sealed class FakeProvider : IEmbeddingProvider
{
    private int _batchIndex;

    public FakeProvider(string name) => Name = name;

    public string Name { get; }

    public bool IsConfigured { get; set; } = true;

    public string ModelId { get; set; } = "test-model";

    public int MaxBatchSize { get; set; } = 4096;

    /// <summary>Documents passed to each successive call.</summary>
    public List<int> BatchSizes { get; } = new();

    public int Calls { get; private set; }

    /// <summary>
    /// Corpus offsets whose batch always fails, identified by the batch's first document rather
    /// than by a call counter — a retry must count as the same batch, not the next one.
    /// </summary>
    public HashSet<int> FailAtOffset { get; } = new();

    /// <summary>How many leading attempts return null before succeeding.</summary>
    public int FailFirstAttempts { get; set; }

    /// <summary>Corpus offset whose batch returns fewer vectors than it was given.</summary>
    public int ShortResultAtOffset { get; set; } = -1;

    /// <summary>Invoked with the 1-based batch ordinal before each call.</summary>
    public Action<int>? OnBatch { get; set; }

    /// <summary>Cancels partway through producing a batch.</summary>
    public CancellationTokenSource? CancelDuringBatch { get; set; }

    private static int Offset(string document) =>
        int.Parse(document.AsSpan("doc-".Length), provider: CultureInfo.InvariantCulture);

    public static int Decode(ContentVector vector)
    {
        var values = vector.DenseValues!;
        return (int)Math.Round(values[0] / values[1]);
    }

    public Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        Calls++;

        if (FailFirstAttempts > 0)
        {
            FailFirstAttempts--;
            return Task.FromResult<IReadOnlyList<ContentVector>?>(null);
        }

        var offset = Offset(documents[0]);
        _batchIndex++;
        BatchSizes.Add(documents.Count);
        OnBatch?.Invoke(_batchIndex);

        if (CancelDuringBatch is { } cts)
        {
            cts.Cancel();
        }

        ct.ThrowIfCancellationRequested();

        if (FailAtOffset.Contains(offset))
        {
            return Task.FromResult<IReadOnlyList<ContentVector>?>(null);
        }

        var vectors = documents
            .Select(d => ContentVector.Dense(new[] { (float)Offset(d), 1f }))
            .ToList();

        if (offset == ShortResultAtOffset && vectors.Count > 1)
        {
            vectors.RemoveAt(vectors.Count - 1);
        }

        return Task.FromResult<IReadOnlyList<ContentVector>?>(vectors);
    }
}

/// <summary>Captures raised alerts so a test can assert on operator-visible degradation.</summary>
internal sealed class RecordingAlerts : IEngineAlerts
{
    private readonly Dictionary<string, EngineAlert> _active = new(StringComparer.Ordinal);

    public void Raise(string key, string level, string title, string detail = "")
    {
        var now = DateTime.UtcNow;
        _active[key] = _active.TryGetValue(key, out var existing)
            ? existing with { Level = level, Title = title, Detail = detail, LastSeenUtc = now, Count = existing.Count + 1 }
            : new EngineAlert(key, level, title, detail, now, now, 1);
    }

    public void Clear(string key) => _active.Remove(key);

    public IReadOnlyList<EngineAlert> Active() => _active.Values.ToList();
}

/// <summary>An <see cref="IProgress{T}"/> that runs inline, so a test can act on a report deterministically.</summary>
internal sealed class ImmediateProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public ImmediateProgress(Action<T> report) => _report = report;

    public void Report(T value) => _report(value);
}

/// <summary>Concrete OpenAI-shaped provider, so the shared base's parsing can be tested directly.</summary>
internal sealed class TestOpenAiProvider : OpenAiCompatibleEmbeddingProvider
{
    public TestOpenAiProvider(IHttpClientFactory factory, RecordingMetrics metrics)
        : base(factory, metrics, NullLogger<TestOpenAiProvider>.Instance)
    {
    }

    public override string Name => "test-openai";

    protected override string EndpointUrl => "https://example.invalid/v1/embeddings";

    protected override string? ApiKey => "key";

    protected override string Model => "test-model";
}
