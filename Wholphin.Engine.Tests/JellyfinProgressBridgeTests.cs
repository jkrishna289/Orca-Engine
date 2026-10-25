using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Home;
using Wholphin.Engine.Playback;
using Xunit;

namespace Wholphin.Engine.Tests;

public sealed class JellyfinProgressBridgeTests
{
    [Fact]
    public async Task UnwatchedLibraryTitleResolvesItsIdWithoutInventingResumeProgress()
    {
        using var fixture = new BridgeFixture();
        var movie = fixture.AddMovie();
        await fixture.Bridge.ObserveExistingAsync(fixture.User.Id, fixture.Identity);
        var progress = await fixture.Progress.GetAsync(fixture.User.Id, fixture.Identity);
        Assert.NotNull(progress);
        Assert.Equal(movie.Id, progress.JellyfinItemId);
        Assert.Equal(0, progress.PositionMs);
        Assert.False(progress.Completed);
        Assert.Empty(await fixture.Resume.GetResumeAsync(fixture.User.Id, 10, default));
    }

    [Fact]
    public async Task StreamSurvivesWithoutLibraryThenTransfersFortyPercentToDifferentRuntimeOnce()
    {
        using var fixture = new BridgeFixture();
        var session = await fixture.StartStreamAsync();
        await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(0, 48 * 60_000, 120 * 60_000, false, true));
        await fixture.Bridge.ReconcileAsync();
        Assert.Single(await fixture.Progress.GetPendingAsync());
        var movie = fixture.AddMovie(runtimeMinutes: 150);
        await fixture.Bridge.ReconcileAsync();
        var data = fixture.Data[movie.Id];
        Assert.Equal(TimeSpan.FromMinutes(60).Ticks, data.PlaybackPositionTicks);
        Assert.False(data.Played);
        Assert.Equal(1, fixture.Saves);
        Assert.Equal(movie.Id, (await fixture.Progress.GetAsync(fixture.User.Id, fixture.Identity))!.JellyfinItemId);
        await fixture.Bridge.ReconcileAsync();
        await fixture.Bridge.ReconcileUserAsync(fixture.User.Id);
        Assert.Equal(1, fixture.Saves);
    }

    [Fact]
    public async Task NewerLibraryPlaybackWinsAndRetiresTheOldStreamWriter()
    {
        using var fixture = new BridgeFixture();
        var session = await fixture.StartStreamAsync();
        await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(0, 40_000, 100_000, false, false));
        var movie = fixture.AddMovie();
        fixture.Data[movie.Id].PlaybackPositionTicks = TimeSpan.FromMinutes(72).Ticks;
        fixture.Data[movie.Id].LastPlayedDate = DateTime.UtcNow.AddMinutes(1);
        await fixture.Bridge.ReconcileAsync();
        var record = (await fixture.Progress.GetAsync(fixture.User.Id, fixture.Identity))!;
        Assert.Equal(0.6, (double)record.PositionMs / record.DurationMs, 6);
        Assert.Equal(0, fixture.Saves);
        Assert.Equal(PlaybackReportResult.Superseded,
            await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(1, 41_000, 100_000, false, false)));
    }

    [Fact]
    public async Task LocalResetEventPreventsAnOlderStreamFromReappearing()
    {
        using var fixture = new BridgeFixture();
        var movie = fixture.AddMovie();
        await fixture.Bridge.StartAsync(default);
        var session = await fixture.StartStreamAsync();
        await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(0, 40_000, 100_000, false, false));
        await fixture.Bridge.ReconcileAsync();
        fixture.Data[movie.Id].PlaybackPositionTicks = 0;
        fixture.Data[movie.Id].Played = false;
        fixture.Publish(movie, UserDataSaveReason.TogglePlayed);
        Assert.Equal(PlaybackReportResult.Superseded,
            await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(1, 41_000, 100_000, false, false)));
        await fixture.Bridge.ReconcileUserAsync(fixture.User.Id);
        Assert.Equal(0, fixture.Data[movie.Id].PlaybackPositionTicks);
        Assert.Empty(await fixture.Resume.GetResumeAsync(fixture.User.Id, 10, default));
        await fixture.Bridge.StopAsync(default);
    }

    [Fact]
    public async Task ImportEventsDoNotRetireTheActiveStreamAndCompletionClearsResume()
    {
        using var fixture = new BridgeFixture();
        var movie = fixture.AddMovie();
        await fixture.Bridge.StartAsync(default);
        var session = await fixture.StartStreamAsync();
        await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(0, 40_000, 100_000, false, false));
        await fixture.Bridge.ReconcileAsync();
        Assert.Equal(PlaybackReportResult.Accepted,
            await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(1, 100_000, 100_000, true, true)));
        await fixture.Bridge.ReconcileAsync();
        Assert.True(fixture.Data[movie.Id].Played);
        Assert.Equal(0, fixture.Data[movie.Id].PlaybackPositionTicks);
        Assert.Empty(await fixture.Resume.GetResumeAsync(fixture.User.Id, 10, default));
        await fixture.Bridge.StopAsync(default);
    }

    [Fact]
    public async Task ContinueWatchingHasOneEntryBeforeAndAfterLibraryHandoff()
    {
        using var fixture = new BridgeFixture();
        var session = await fixture.StartStreamAsync();
        await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(0, 40_000, 100_000, false, true));
        var streamed = Assert.Single(await fixture.Resume.GetResumeAsync(fixture.User.Id, 10, default));
        Assert.Null(streamed.Item.JellyfinItemId);
        Assert.Equal("Example movie", streamed.Item.Title);
        Assert.Equal(0.4, streamed.Progress, 6);
        var movie = fixture.AddMovie();
        var local = Assert.Single(await fixture.Resume.GetResumeAsync(fixture.User.Id, 10, default));
        Assert.Equal(movie.Id, local.Item.JellyfinItemId);
        Assert.Equal(0.4, local.Progress, 6);
        // A second edition is still the same title, not a second Continue Watching card.
        var edition = fixture.AddMovie();
        fixture.Data[edition.Id].PlaybackPositionTicks = TimeSpan.FromMinutes(12).Ticks;
        fixture.Data[edition.Id].LastPlayedDate = DateTime.UtcNow.AddDays(-1);
        Assert.Single(await fixture.Resume.GetResumeAsync(fixture.User.Id, 10, default));
    }

    [Fact]
    public async Task ExistingLocalProgressSeedsAFirstStreamSession()
    {
        using var fixture = new BridgeFixture();
        var movie = fixture.AddMovie();
        fixture.Data[movie.Id].PlaybackPositionTicks = TimeSpan.FromMinutes(72).Ticks;
        fixture.Data[movie.Id].LastPlayedDate = DateTime.UtcNow.AddDays(-1);
        await fixture.Bridge.ObserveExistingAsync(fixture.User.Id, fixture.Identity);
        var session = await fixture.StartStreamAsync();
        Assert.Equal(0.6, (double)session.PositionMs / session.DurationMs, 6);
        Assert.False(session.SessionStopped);
    }

    [Fact]
    public async Task MissingRuntimeRetriesAndSeriesNeverReceivesMovieResume()
    {
        using var fixture = new BridgeFixture();
        var session = await fixture.StartStreamAsync();
        await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(0, 40_000, 100_000, false, true));
        fixture.Items.Add(new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "10" } });
        var movie = fixture.AddMovie();
        movie.RunTimeTicks = null;
        await fixture.Bridge.ReconcileAsync();
        Assert.Equal(0, fixture.Saves);
        movie.RunTimeTicks = TimeSpan.FromMinutes(100).Ticks;
        await fixture.Bridge.ReconcileAsync();
        Assert.Equal(TimeSpan.FromMinutes(40).Ticks, fixture.Data[movie.Id].PlaybackPositionTicks);
        Assert.Equal(1, fixture.Saves);
    }

    [Fact]
    public async Task FailedJellyfinSaveLeavesRevisionPendingForRetry()
    {
        using var fixture = new BridgeFixture();
        var session = await fixture.StartStreamAsync();
        await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(0, 40_000, 100_000, false, true));
        fixture.AddMovie();
        fixture.FailSave = true;
        await fixture.Bridge.ReconcileAsync();
        Assert.Single(await fixture.Progress.GetPendingAsync());
        fixture.FailSave = false;
        await fixture.Bridge.ReconcileAsync();
        Assert.Empty(await fixture.Progress.GetPendingAsync());
    }

    [Fact]
    public async Task OfflineUnwatchedClearsOldResume()
    {
        using var fixture = new BridgeFixture();
        var movie = fixture.AddMovie();
        var session = await fixture.StartStreamAsync();
        await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(0, 40_000, 100_000, false, true));
        await fixture.Bridge.ReconcileAsync();
        // Jellyfin resets user data while the plugin is not subscribed.
        fixture.Data[movie.Id].PlaybackPositionTicks = 0;
        fixture.Data[movie.Id].Played = false;
        fixture.Data[movie.Id].LastPlayedDate = null;
        fixture.Data[movie.Id].PlayCount = 0;
        await fixture.Bridge.ObserveExistingAsync(fixture.User.Id, fixture.Identity);
        await fixture.Bridge.ReconcileUserAsync(fixture.User.Id);
        Assert.Empty(await fixture.Resume.GetResumeAsync(fixture.User.Id, 10, default));
    }

    [Fact]
    public async Task SettledRecordsDoNotWaitOnTheWriteGate()
    {
        using var fixture = new BridgeFixture();
        fixture.AddMovie();
        var session = await fixture.StartStreamAsync();
        await fixture.Progress.ReportAsync(fixture.User.Id, session.ActiveSessionId, new(0, 40_000, 100_000, false, true));
        await fixture.Bridge.ReconcileAsync();
        Assert.Empty(await fixture.Progress.GetPendingAsync());

        // Hold the gate; a home read reconciling an already-applied record must not queue behind it.
        var release = new TaskCompletionSource<bool>();
        var held = fixture.Progress.ReconcileAsync(session.Id, (_, _) => release.Task);
        var read = fixture.Bridge.ReconcileUserAsync(fixture.User.Id);
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))));
        release.SetResult(false);
        await held;
    }

    private sealed class BridgeFixture : IWholphinDbContextFactory, IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private EventHandler<UserDataSaveEventArgs>? _saved;
        public User User { get; } = new("viewer", "auth", "reset") { Id = Guid.NewGuid() };
        public PlaybackIdentity Identity { get; } = new(10, "movie");
        public List<BaseItem> Items { get; } = [];
        public Dictionary<Guid, UserItemData> Data { get; } = [];
        public PlaybackProgressService Progress { get; }
        public JellyfinProgressBridge Bridge { get; }
        public JellyfinResumeProvider Resume { get; }
        public int Saves { get; private set; }
        public bool FailSave { get; set; }
        public string DatabasePath => ":memory:";

        public BridgeFixture()
        {
            _connection.Open();
            using (var db = Create()) db.Database.EnsureCreated();
            var library = ForwardingProxy.Create<ILibraryManager>((method, args) => method.Name switch
            {
                "GetItemList" => Items,
                "GetItemById" => Items.FirstOrDefault(i => i.Id == (Guid)args![0]!),
                _ => throw new NotSupportedException(method.Name),
            });
            var users = ForwardingProxy.Create<IUserManager>((method, args) => method.Name == "GetUserById" && (Guid)args![0]! == User.Id ? User : null);
            var data = ForwardingProxy.Create<IUserDataManager>((method, args) =>
            {
                switch (method.Name)
                {
                    case "GetUserData": return Data[((BaseItem)args![1]!).Id];
                    case "SaveUserData":
                        if (FailSave) throw new IOException("Injected unavailable Jellyfin user data store.");
                        Saves++;
                        Publish((BaseItem)args![1]!, (UserDataSaveReason)args[3]!);
                        return null;
                    case "add_UserDataSaved": _saved += (EventHandler<UserDataSaveEventArgs>)args![0]!; return null;
                    case "remove_UserDataSaved": _saved -= (EventHandler<UserDataSaveEventArgs>)args![0]!; return null;
                    default: throw new NotSupportedException(method.Name);
                }
            });
            Progress = new PlaybackProgressService(this);
            Bridge = new JellyfinProgressBridge(Progress, library, users, data, NullLogger<JellyfinProgressBridge>.Instance);
            Resume = new JellyfinResumeProvider(library, users, data, Progress, Bridge, this);
        }

        public Task<PlaybackProgressRecord> StartStreamAsync() => Progress.StartAsync(User.Id, Identity, "Example movie", 2020);

        public Movie AddMovie(int runtimeMinutes = 120)
        {
            var movie = new Movie { Id = Guid.NewGuid(), Name = "Example movie", ProviderIds = new() { ["Tmdb"] = "10" }, RunTimeTicks = TimeSpan.FromMinutes(runtimeMinutes).Ticks };
            Items.Add(movie);
            Data.Add(movie.Id, new UserItemData { Key = movie.Id.ToString() });
            return movie;
        }

        public void Publish(BaseItem item, UserDataSaveReason reason) => _saved?.Invoke(this,
            new UserDataSaveEventArgs { UserId = User.Id, Item = item, UserData = Data[item.Id], SaveReason = reason });

        public WholphinDbContext Create() => new(new DbContextOptionsBuilder<WholphinDbContext>().UseSqlite(_connection).Options);
        public void Dispose() => _connection.Dispose();
    }

    public class ForwardingProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> _call = null!;
        public static T Create<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
        {
            var proxy = Create<T, ForwardingProxy>();
            ((ForwardingProxy)(object)proxy)._call = call;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _call(targetMethod!, args);
    }
}
