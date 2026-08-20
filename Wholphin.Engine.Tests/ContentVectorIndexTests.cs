using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Recommendation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The index rebuild lifecycle. The behaviour under test is what happens on a bad day: a rebuild
/// that fails must cost nothing, because the naive shape caches the empty result and silently turns
/// off content similarity for the whole TTL.
/// </summary>
public sealed class ContentVectorIndexTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbFactory _factory;
    private readonly InMemoryCache _cache;

    public ContentVectorIndexTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _factory = new TestDbFactory(_connection);
        _cache = new InMemoryCache(new RecordingMetrics());

        using var db = _factory.Create();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ASuccessfulRebuildBecomesTheActiveIndex()
    {
        var ids = await SeedAsync(5);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));

        var snapshot = await Index(embeddings).GetAsync();

        Assert.Equal(5, snapshot.Count);
        Assert.Equal("cloud", snapshot.ProviderName);
        Assert.All(ids, id => Assert.False(snapshot.VectorFor(id).IsEmpty));
    }

    [Fact]
    public async Task AFailedRebuildKeepsTheIndexThatAlreadyWorked()
    {
        await SeedAsync(5);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));
        var index = Index(embeddings);

        var good = await index.GetAsync();
        Assert.Equal(5, good.Count);

        // A new title makes the next rebuild need the provider, which then fails outright.
        await SeedAsync(1);
        embeddings.Next = _ => Run.Failed();
        Expire(embeddings.ActiveProviderName);

        var after = await index.GetAsync();

        // Same object, not merely an equivalent one: the previous index was never replaced.
        Assert.Same(good, after);
        Assert.Equal(5, after.Count);
    }

    // ---- persistence ---------------------------------------------------------------------------

    [Fact]
    public async Task ARestartReusesStoredVectorsWithoutEmbeddingAnything()
    {
        var ids = await SeedAsync(12);
        var first = new ScriptedEmbeddings(count => Run.Ok(count));
        await Index(first).GetAsync();
        Assert.Equal(1, first.Calls);

        // A restart: fresh index object (no in-process last-good) and an empty cache. Before the
        // vectors were persisted this re-embedded the entire catalog, every single time.
        Expire(first.ActiveProviderName);
        var afterRestart = new ScriptedEmbeddings(_ => throw new InvalidOperationException("must not embed"));
        var snapshot = await Index(afterRestart).GetAsync();

        Assert.Equal(0, afterRestart.Calls);
        Assert.Equal(12, snapshot.Count);
        Assert.All(ids, id => Assert.False(snapshot.VectorFor(id).IsEmpty));
    }

    [Fact]
    public async Task OnlyTheNewTitlesAreEmbeddedOnTheNextRebuild()
    {
        await SeedAsync(10);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));
        var index = Index(embeddings);
        await index.GetAsync();

        await SeedAsync(3);
        Expire(embeddings.ActiveProviderName);
        embeddings.Next = count => Run.Ok(count);

        var snapshot = await index.GetAsync();

        Assert.Equal(13, snapshot.Count);
        Assert.Equal(new[] { 10, 3 }, embeddings.CorpusSizes);
    }

    [Fact]
    public async Task EditingATitleReEmbedsThatTitleAndNothingElse()
    {
        var ids = await SeedAsync(6);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));
        var index = Index(embeddings);
        await index.GetAsync();

        // The document changed — which is exactly what backfilling a title's language does, and the
        // stored vector predates it. Serving that vector would silently ignore the new metadata.
        await using (var db = _factory.Create())
        {
            var item = await db.CatalogItems.FirstAsync(c => c.Id == ids[2]);
            item.OriginalLanguage = "hi";
            await db.SaveChangesAsync();
        }

        Expire(embeddings.ActiveProviderName);
        await index.GetAsync();

        Assert.Equal(new[] { 6, 1 }, embeddings.CorpusSizes);
    }

    [Fact]
    public async Task ChangingTheModelInvalidatesEveryStoredVector()
    {
        await SeedAsync(8);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));
        await Index(embeddings).GetAsync();

        // Different model, same provider: different dimensions and different geometry, so every
        // stored vector is worthless and must be re-made rather than reused.
        embeddings.ActiveModelId = "some-other-model";
        Expire(embeddings.ActiveProviderName);

        var snapshot = await Index(embeddings).GetAsync();

        Assert.Equal(8, snapshot.Count);
        Assert.Equal(new[] { 8, 8 }, embeddings.CorpusSizes);
    }

    [Fact]
    public async Task VectorsForDeletedTitlesArePruned()
    {
        var ids = await SeedAsync(5);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));
        var index = Index(embeddings);
        await index.GetAsync();

        Assert.Equal(5, await new VectorStore(_factory).CountAsync());

        await using (var db = _factory.Create())
        {
            await db.CatalogItems.Where(c => c.Id == ids[0]).ExecuteDeleteAsync();
        }

        await SeedAsync(1);
        Expire(embeddings.ActiveProviderName);
        await index.GetAsync();

        // Without pruning the table only ever grows, keeping a vector per title ever imported.
        Assert.Equal(5, await new VectorStore(_factory).CountAsync());
    }

    [Fact]
    public async Task AStoredVectorSurvivesTheRoundTripIntact()
    {
        var ids = await SeedAsync(4);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));
        var before = await Index(embeddings).GetAsync();

        Expire(embeddings.ActiveProviderName);
        var after = await Index(new ScriptedEmbeddings(_ => Run.Failed())).GetAsync();

        // Same numbers out of the database as went in — a blob written or read wrongly would show
        // up here as a vector that no longer matches itself.
        foreach (var id in ids)
        {
            Assert.Equal(1.0, ContentVector.Cosine(before.VectorFor(id), after.VectorFor(id)), 5);
        }
    }

    [Fact]
    public async Task WithNoPreviousIndexAFailedRebuildYieldsAnEmptyOneRatherThanThrowing()
    {
        await SeedAsync(5);
        var embeddings = new ScriptedEmbeddings(_ => Run.Failed());

        var snapshot = await Index(embeddings).GetAsync();

        Assert.Equal(0, snapshot.Count);
        Assert.True(snapshot.VectorFor(1).IsEmpty);
    }

    [Fact]
    public async Task ASecondCallIsServedFromCacheWithoutReEmbedding()
    {
        await SeedAsync(5);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));
        var index = Index(embeddings);

        await index.GetAsync();
        await index.GetAsync();

        Assert.Equal(1, embeddings.Calls);
    }

    [Fact]
    public async Task ConcurrentCallersCoalesceIntoOneRebuild()
    {
        await SeedAsync(20);
        var gate = new TaskCompletionSource();
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count)) { Gate = gate.Task };
        var index = Index(embeddings);

        // Without coalescing each of these embeds the entire catalog independently.
        var callers = Enumerable.Range(0, 8).Select(_ => index.GetAsync()).ToArray();
        gate.SetResult();
        var snapshots = await Task.WhenAll(callers);

        Assert.Equal(1, embeddings.Calls);
        Assert.All(snapshots, s => Assert.Equal(20, s.Count));
    }

    [Fact]
    public async Task AnEmptyCatalogNeverCallsTheProvider()
    {
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));

        var snapshot = await Index(embeddings).GetAsync();

        Assert.Equal(0, snapshot.Count);
        Assert.Equal(0, embeddings.Calls);
    }

    [Fact]
    public async Task UnavailableItemsAreNotIndexed()
    {
        await SeedAsync(3);
        await SeedAsync(2, AvailabilityState.Unavailable);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));

        var snapshot = await Index(embeddings).GetAsync();

        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public async Task VectorsLandOnTheItemThatProducedThem()
    {
        var ids = await SeedAsync(6);
        var embeddings = new ScriptedEmbeddings(count => Run.Ok(count));

        var snapshot = await Index(embeddings).GetAsync();

        // Run.Ok encodes each vector with its position, and the index pairs position i with ids[i].
        for (var i = 0; i < ids.Count; i++)
        {
            Assert.Equal(i, FakeProvider.Decode(snapshot.VectorFor(ids[i])));
        }
    }

    [Fact]
    public async Task CancellationDoesNotPublishAHalfBuiltIndex()
    {
        await SeedAsync(5);
        using var cts = new CancellationTokenSource();
        var embeddings = new ScriptedEmbeddings(_ => throw new OperationCanceledException());
        var index = Index(embeddings);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.GetAsync(cts.Token));

        // Nothing was cached, so a later healthy call rebuilds rather than serving a cancelled run.
        embeddings.Next = count => Run.Ok(count);
        var snapshot = await index.GetAsync();
        Assert.Equal(5, snapshot.Count);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private ContentVectorIndex Index(IEmbeddingService embeddings) => new(
        _factory,
        _cache,
        embeddings,
        new VectorStore(_factory),
        new RecordingMetrics(),
        NullLogger<ContentVectorIndex>.Instance);

    private void Expire(string providerName) => _cache.Remove($"contentvectors:{providerName}");

    private async Task<List<long>> SeedAsync(int count, AvailabilityState availability = AvailabilityState.WatchNow)
    {
        await using var db = _factory.Create();
        var items = Enumerable.Range(0, count)
            .Select(i => new CatalogItem
            {
                Title = $"Title {i}",
                MediaType = MediaType.Movie,
                Availability = availability,
                Overview = $"Overview {i}",
            })
            .ToList();

        db.CatalogItems.AddRange(items);
        await db.SaveChangesAsync();
        return items.Select(i => i.Id).ToList();
    }

    /// <summary>Canned <see cref="EmbeddingRun"/> shapes, so the tests read as lifecycle not plumbing.</summary>
    private static class Run
    {
        public static EmbeddingRun Ok(int count) => new(
            Enumerable.Range(0, count).Select(i => ContentVector.Dense(new[] { (float)i, 1f })).ToList(),
            "cloud",
            count,
            96,
            1,
            0,
            null);

        public static EmbeddingRun Failed() =>
            EmbeddingRun.Failed("cloud", 5, 96, 0, 1, "provider unavailable");
    }
}

/// <summary>
/// A real SQLite database over one shared in-memory connection, so every context the factory hands
/// out sees the same data. The EF in-memory provider would happily fake LINQ this code relies on.
/// </summary>
internal sealed class TestDbFactory : IWholphinDbContextFactory
{
    private readonly SqliteConnection _connection;

    public TestDbFactory(SqliteConnection connection) => _connection = connection;

    public string DatabasePath => ":memory:";

    public WholphinDbContext Create() => new(
        new DbContextOptionsBuilder<WholphinDbContext>().UseSqlite(_connection).Options);
}

/// <summary>An <see cref="IEmbeddingService"/> whose outcome each test dictates.</summary>
internal sealed class ScriptedEmbeddings : IEmbeddingService
{
    private int _calls;

    public ScriptedEmbeddings(Func<int, EmbeddingRun> next) => Next = next;

    public string ActiveProviderName { get; set; } = "cloud";

    public string ActiveModelId { get; set; } = "test-model";

    /// <summary>Produces the run for a corpus of the given size.</summary>
    public Func<int, EmbeddingRun> Next { get; set; }

    /// <summary>Held before answering, so a test can pile up concurrent callers first.</summary>
    public Task? Gate { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>How many documents each successive corpus call was given — the incremental proof.</summary>
    public List<int> CorpusSizes { get; } = new();

    public Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ContentVector>?>(Next(documents.Count).Vectors);

    public async Task<EmbeddingRun> EmbedCorpusAsync(
        IReadOnlyList<string> documents,
        IProgress<EmbeddingProgress>? progress = null,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        lock (CorpusSizes)
        {
            CorpusSizes.Add(documents.Count);
        }

        if (Gate is { } gate)
        {
            await gate.ConfigureAwait(false);
        }

        return Next(documents.Count);
    }
}
