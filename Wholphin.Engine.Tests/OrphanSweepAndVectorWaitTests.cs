using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Discovery;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Recommendation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Two ways a title page went blank or slow: the sweep deleting a title still on screen, and More
/// Like This waiting on a vector-index rebuild.
/// </summary>
public class OrphanSweepAndVectorWaitTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestDbFactory _factory;

    public OrphanSweepAndVectorWaitTests()
    {
        _connection.Open();
        _factory = new TestDbFactory(_connection);
        using var db = _factory.Create();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static CatalogItem Requestable(int tmdbId, DateTime lastSynced) => new()
    {
        TmdbId = tmdbId,
        MediaType = MediaType.Series,
        Title = $"Title {tmdbId}",
        Availability = AvailabilityState.Request,
        LastSyncedAt = lastSynced,
    };

    [Fact]
    public void SweepKeepsRecentlyImportedTitlesAndDeletesOnlyLongUntouchedOnes()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        using (var db = _factory.Create())
        {
            db.CatalogItems.AddRange(
                Requestable(1, now.AddHours(-2)),     // just imported by More Like This
                Requestable(2, now.AddDays(-13)),     // inside the grace period
                Requestable(3, now.AddDays(-30)));    // long untouched, referenced by nothing
            db.SaveChanges();
        }

        using var read = _factory.Create();
        var doomed = DiscoveryPipeline.Orphans(read, now).Select(c => c.TmdbId).ToList();

        Assert.Equal(new int?[] { 3 }, doomed);
    }

    [Fact]
    public async Task MoreLikeThisUsesTheBuiltIndexInsteadOfWaitingForARebuild()
    {
        var built = ContentVectorSnapshot.Empty("cloud");
        var index = new SlowIndex { Current = built };

        var vectors = await VectorsWithTimeout(index);

        Assert.Same(built, vectors);
        Assert.True(index.RebuildStarted); // the refresh still happens, just not on this request
    }

    [Fact]
    public async Task WithNothingBuiltYetItWaitsForTheFirstBuild()
    {
        var index = new SlowIndex();
        var pending = SimilarityService.VectorsWithoutWaitingAsync(index, CancellationToken.None);
        Assert.False(pending.IsCompleted);

        var fresh = ContentVectorSnapshot.Empty("cloud");
        index.Finish(fresh);

        Assert.Same(fresh, await pending);
    }

    private static async Task<ContentVectorSnapshot> VectorsWithTimeout(IContentVectorIndex index)
    {
        var task = SimilarityService.VectorsWithoutWaitingAsync(index, CancellationToken.None);
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(2000)));
        return await task;
    }

    /// <summary>An index whose rebuild only finishes when the test says so.</summary>
    private sealed class SlowIndex : IContentVectorIndex
    {
        private readonly TaskCompletionSource<ContentVectorSnapshot> _rebuild = new();

        public bool RebuildStarted { get; private set; }

        public ContentVectorSnapshot? Current { get; set; }

        public Task<ContentVectorSnapshot> GetAsync(CancellationToken ct = default)
        {
            RebuildStarted = true;
            return _rebuild.Task;
        }

        public void Finish(ContentVectorSnapshot snapshot) => _rebuild.SetResult(snapshot);
    }
}
