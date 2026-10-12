using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Playback;
using Xunit;

namespace Wholphin.Engine.Tests;

public sealed class PlaybackProgressTests : IDisposable
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUser = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly PlaybackIdentity Movie = new(550, "movie");
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestDbFactory _factory;
    private readonly PlaybackProgressService _service;

    public PlaybackProgressTests()
    {
        _connection.Open();
        _factory = new TestDbFactory(_connection);
        using var db = _factory.Create();
        db.Database.EnsureCreated();
        _service = new PlaybackProgressService(_factory);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ResumePersistsAcrossServiceRecreationAndNewPlaybackLease()
    {
        var first = await _service.StartAsync(User, Movie, "Fight Club", 1999);
        Assert.Equal(PlaybackReportResult.Accepted, await _service.ReportAsync(User, first.ActiveSessionId, Update(1, 40000)));
        var restarted = new PlaybackProgressService(_factory);
        var saved = await restarted.GetAsync(User, Movie);
        Assert.Equal(40000, saved!.PositionMs);
        Assert.Equal(100000, saved.DurationMs);
        var resumed = await restarted.StartAsync(User, Movie);
        Assert.Equal(40000, resumed.PositionMs);
        Assert.NotEqual(first.ActiveSessionId, resumed.ActiveSessionId);
        Assert.Equal(PlaybackReportResult.NotFound, await restarted.ReportAsync(User, first.ActiveSessionId, Update(2, 90000)));
        Assert.Equal(40000, (await restarted.GetAsync(User, Movie))!.PositionMs);
    }

    [Fact]
    public async Task UsersCannotWriteAnotherUsersLeaseOrShareResumeState()
    {
        var first = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, first.ActiveSessionId, Update(1, 40000));
        Assert.Equal(PlaybackReportResult.NotFound, await _service.ReportAsync(OtherUser, first.ActiveSessionId, Update(2, 90000)));
        Assert.Null(await _service.GetAsync(OtherUser, Movie));
        var other = await _service.StartAsync(OtherUser, Movie);
        Assert.Equal(0, other.PositionMs);
        Assert.Equal(40000, (await _service.GetAsync(User, Movie))!.PositionMs);
        Assert.Empty(await _service.GetRecentAsync(OtherUser));
    }

    [Fact]
    public async Task OutOfOrderAndDuplicateReportsDoNotChangePositionButNewerBackwardSeekDoes()
    {
        var row = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, row.ActiveSessionId, Update(10, 40000));
        Assert.Equal(PlaybackReportResult.Duplicate, await _service.ReportAsync(User, row.ActiveSessionId, Update(9, 80000)));
        Assert.Equal(PlaybackReportResult.Duplicate, await _service.ReportAsync(User, row.ActiveSessionId, Update(10, 90000)));
        Assert.Equal(40000, (await _service.GetAsync(User, Movie))!.PositionMs);
        await _service.ReportAsync(User, row.ActiveSessionId, Update(11, 20000));
        Assert.Equal(20000, (await _service.GetAsync(User, Movie))!.PositionMs);
        Assert.Equal(2, (await _service.GetAsync(User, Movie))!.Revision);
    }

    [Fact]
    public async Task CompletedPlaybackStaysCompleteUntilNewLeaseActuallyReportsRewatch()
    {
        var first = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, first.ActiveSessionId, Update(1, 100000) with { Completed = true, Stopped = true });
        var rewatch = await _service.StartAsync(User, Movie);
        Assert.True(rewatch.Completed);
        await _service.ReportAsync(User, rewatch.ActiveSessionId, Update(0, 0));
        var saved = await _service.GetAsync(User, Movie);
        Assert.False(saved!.Completed);
        Assert.Equal(0, saved.PositionMs);
    }

    [Fact]
    public async Task StopIsIdempotentAndPreventsLateInFlightProgressFromMovingResumePoint()
    {
        var row = await _service.StartAsync(User, Movie);
        var stopped = Update(3, 40000) with { Stopped = true };
        await _service.ReportAsync(User, row.ActiveSessionId, stopped);
        Assert.Equal(PlaybackReportResult.Duplicate, await _service.ReportAsync(User, row.ActiveSessionId, stopped));
        Assert.Equal(PlaybackReportResult.Superseded, await _service.ReportAsync(User, row.ActiveSessionId, Update(4, 42000)));
        Assert.Equal(40000, (await _service.GetAsync(User, Movie))!.PositionMs);
    }

    [Fact]
    public async Task NativeResetWinsAndRetiresOldStreamLease()
    {
        var row = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, row.ActiveSessionId, Update(1, 40000));
        var now = (await _service.GetAsync(User, Movie))!.UpdatedAt;
        var itemId = Guid.NewGuid();
        await _service.ObserveLocalAsync(User, Movie, itemId, 0, 100000, false, now.AddTicks(1));
        Assert.Equal(PlaybackReportResult.Superseded, await _service.ReportAsync(User, row.ActiveSessionId, Update(2, 45000)));
        var saved = await _service.GetAsync(User, Movie);
        Assert.Equal(0, saved!.PositionMs);
        Assert.Equal(itemId, saved.JellyfinItemId);
        Assert.Equal(saved.Revision, saved.AppliedRevision);
        var rewatch = await _service.StartAsync(User, Movie);
        Assert.Equal(PlaybackReportResult.Accepted, await _service.ReportAsync(User, rewatch.ActiveSessionId, Update(0, 5000)));
    }

    [Fact]
    public async Task OlderNativeObservationCannotOverwriteNewerStreamProgress()
    {
        var row = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, row.ActiveSessionId, Update(1, 40000));
        var saved = await _service.GetAsync(User, Movie);
        await _service.ObserveLocalAsync(User, Movie, Guid.NewGuid(), 1000, 100000, true, saved!.UpdatedAt.AddDays(-1));
        Assert.Equal(40000, (await _service.GetAsync(User, Movie))!.PositionMs);
        Assert.Equal(PlaybackReportResult.Accepted, await _service.ReportAsync(User, row.ActiveSessionId, Update(2, 45000)));
    }

    [Fact]
    public async Task UnknownDurationCannotErasePreviouslySavedProgress()
    {
        var row = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, row.ActiveSessionId, Update(1, 40000));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ReportAsync(User, row.ActiveSessionId, Update(2, 0) with { DurationMs = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ReportAsync(User, row.ActiveSessionId, Update(2, 0) with { DurationMs = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ReportAsync(User, row.ActiveSessionId, Update(2, 100001)));
        Assert.Equal(40000, (await _service.GetAsync(User, Movie))!.PositionMs);
    }

    [Fact]
    public async Task DelayedNativeEventFromBeforeNewLeaseCannotRetireNewPlayback()
    {
        var oldObservation = DateTime.UtcNow.AddSeconds(-1);
        var row = await _service.StartAsync(User, Movie);
        await _service.ObserveLocalAsync(User, Movie, Guid.NewGuid(), 10000, 100000, false, oldObservation);
        Assert.Equal(PlaybackReportResult.Accepted, await _service.ReportAsync(User, row.ActiveSessionId, Update(1, 40000)));
        Assert.Equal(40000, (await _service.GetAsync(User, Movie))!.PositionMs);
    }

    [Fact]
    public async Task NewerNativeGenerationWinsEvenWhenOldStreamReportReachedStorageFirst()
    {
        var row = await _service.StartAsync(User, Movie);
        var nativeAt = row.SessionStartedAt;
        await _service.ReportAsync(User, row.ActiveSessionId, Update(1, 90000) with { Stopped = true });
        await _service.ObserveLocalAsync(User, Movie, Guid.NewGuid(), 40000, 100000, false, nativeAt);
        var saved = await _service.GetAsync(User, Movie);
        Assert.Equal(40000, saved!.PositionMs);
        Assert.Equal(PlaybackReportResult.Superseded, await _service.ReportAsync(User, row.ActiveSessionId, Update(2, 95000)));
    }

    [Fact]
    public async Task MovieAndEachEpisodeHaveIndependentIdentityEvenWhenTmdbNumbersMatch()
    {
        var film = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, film.ActiveSessionId, Update(1, 40000));
        var episode = await _service.StartAsync(User, new PlaybackIdentity(550, "tv", 0, 1));
        await _service.ReportAsync(User, episode.ActiveSessionId, Update(1, 20000));
        var next = await _service.StartAsync(User, new PlaybackIdentity(550, "tv", 0, 2));
        Assert.Equal(0, next.PositionMs);
        Assert.Equal(40000, (await _service.GetAsync(User, Movie))!.PositionMs);
        Assert.Equal(20000, (await _service.GetAsync(User, new PlaybackIdentity(550, "tv", 0, 1)))!.PositionMs);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.StartAsync(User, new PlaybackIdentity(550, "tv")));
    }

    [Fact]
    public async Task FailedHandoffRemainsPendingAndSuccessfulHandoffAppliesOnlyItsCurrentRevision()
    {
        var row = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, row.ActiveSessionId, Update(1, 40000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ReconcileAsync(row.Id, (r, ct) => throw new InvalidOperationException("Jellyfin unavailable")));
        Assert.Single(await _service.GetPendingAsync());
        var itemId = Guid.NewGuid();
        await _service.ReconcileAsync(row.Id, (r, ct) => { r.JellyfinItemId = itemId; return Task.FromResult(true); });
        Assert.Empty(await _service.GetPendingAsync());
        await _service.ReportAsync(User, row.ActiveSessionId, Update(2, 50000));
        Assert.Single(await _service.GetPendingAsync());
    }

    [Fact]
    public async Task ReconciliationSerializesAgainstReportsSoNewerProgressRemainsPending()
    {
        var row = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, row.ActiveSessionId, Update(1, 40000));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconcile = _service.ReconcileAsync(row.Id, async (r, ct) =>
        {
            entered.SetResult();
            await release.Task;
            Assert.Equal(40000, r.PositionMs);
            r.JellyfinItemId = Guid.NewGuid();
            return true;
        });
        await entered.Task;
        var report = _service.ReportAsync(User, row.ActiveSessionId, Update(2, 50000));
        Assert.False(report.IsCompleted);
        release.SetResult();
        await reconcile;
        await report;
        var pending = Assert.Single(await _service.GetPendingAsync());
        Assert.Equal(50000, pending.PositionMs);
        Assert.True(pending.Revision > pending.AppliedRevision);
    }

    [Fact]
    public async Task VersionTwelveUpgradeCreatesDurableTableAndPreservesExistingCatalog()
    {
        await using (var db = _factory.Create())
        {
            db.CatalogItems.Add(new CatalogItem { Title = "Existing title", JellyfinItemId = Guid.NewGuid() });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE PlaybackProgress");
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 12");
        }
        var initializer = new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance);
        await initializer.StartAsync(CancellationToken.None);
        var row = await _service.StartAsync(User, Movie);
        await _service.ReportAsync(User, row.ActiveSessionId, Update(1, 40000));
        await initializer.StartAsync(CancellationToken.None);
        await using var upgraded = _factory.Create();
        Assert.Single(await upgraded.CatalogItems.ToListAsync());
        Assert.Equal(40000, (await _service.GetAsync(User, Movie))!.PositionMs);
        await using var version = _connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(DatabaseInitializer.SchemaVersion, Convert.ToInt32(await version.ExecuteScalarAsync()));
    }

    private static PlaybackProgressUpdate Update(long sequence, long position) => new(sequence, position, 100000, false, false);
}
