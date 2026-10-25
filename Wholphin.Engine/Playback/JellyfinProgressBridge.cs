using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Playback;

/// <summary>
/// Uses TMDB identity to hand a user's stream resume point to Jellyfin. Import saves never
/// feed back as new playback; native playback/played toggles invalidate the older stream lease.
/// </summary>
public sealed class JellyfinProgressBridge : IHostedService, IJellyfinProgressBridge
{
    private static readonly BaseItemKind[] Kinds = [BaseItemKind.Movie, BaseItemKind.Episode];
    private readonly IPlaybackProgressService _progress;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILogger<JellyfinProgressBridge> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public JellyfinProgressBridge(
        IPlaybackProgressService progress,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        ILogger<JellyfinProgressBridge> logger)
    {
        _progress = progress;
        _library = library;
        _users = users;
        _userData = userData;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved += OnUserDataSaved;
        _worker = RunAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved -= OnUserDataSaved;
        _stopping.Cancel();
        if (_worker is not null)
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var pending = await _progress.GetPendingAsync(ct).ConfigureAwait(false);
        foreach (var group in pending.GroupBy(p => p.UserId))
        {
            await ReconcileRecordsAsync(group.Key, group, ct).ConfigureAwait(false);
        }
    }

    public async Task ReconcileUserAsync(Guid userId, CancellationToken ct = default)
    {
        // Include already-applied records: native playback may have changed while the plugin
        // was offline. LastPlayedDate is compared before deciding there is nothing to import.
        var records = await _progress.GetRecentAsync(userId, int.MaxValue, ct).ConfigureAwait(false);
        await ReconcileRecordsAsync(userId, records, ct).ConfigureAwait(false);
    }

    public async Task ObserveExistingAsync(Guid userId, PlaybackIdentity identity, CancellationToken ct = default)
    {
        var user = _users.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        var record = await _progress.GetAsync(userId, identity, ct).ConfigureAwait(false);
        var item = AccessibleItems(user).Where(i => Identify(i) == identity)
            .OrderByDescending(i => _userData.GetUserData(user, i)?.LastPlayedDate)
            .FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var data = _userData.GetUserData(user, item);
        if (data is null)
        {
            return;
        }

        var observedAt = data.LastPlayedDate ?? DateTime.MinValue;
        if (record is null || observedAt > record.UpdatedAt)
        {
            await _progress.ObserveLocalAsync(userId, identity, item.Id,
                data.PlaybackPositionTicks / TimeSpan.TicksPerMillisecond,
                (item.RunTimeTicks ?? 0) / TimeSpan.TicksPerMillisecond,
                data.Played, observedAt, ct).ConfigureAwait(false);
        }
    }

    private async Task ReconcileRecordsAsync(Guid userId, IEnumerable<PlaybackProgressRecord> records, CancellationToken ct)
    {
        var user = _users.GetUserById(userId);
        if (user is null || !records.Any())
        {
            return;
        }

        // ponytail: full accessible-library scan per call (every home read); index items by TMDB id if large libraries still miss the row budget.
        // A user-scoped Jellyfin query respects library access and parental restrictions.
        var candidates = AccessibleItems(user).Select(i => (Item: i, Identity: Identify(i)))
            .Where(i => i.Identity is not null).ToLookup(i => i.Identity!, i => i.Item);
        foreach (var snapshot in records)
        {
            ct.ThrowIfCancellationRequested();
            var identity = new PlaybackIdentity(snapshot.TmdbId, snapshot.MediaType, snapshot.Season, snapshot.Episode);
            var matches = candidates[identity].Where(i => i.RunTimeTicks is > 0).ToList();
            if (matches.Count == 0)
            {
                continue; // Metadata/runtime may still be arriving; a scan or timer retries.
            }

            // Home reads reconcile every record; most are settled. Skip the write gate and DB
            // round trip unless the callback below could change something (it re-checks under lock).
            if (IsSettled(snapshot, matches, user))
            {
                continue;
            }

            try
            {
                await _progress.ReconcileAsync(snapshot.Id, (record, token) =>
                {
                    // If there are multiple editions, keep the mapped copy unless another has
                    // newer native playback. Never assign a movie's position to a series.
                    var newest = matches.OrderByDescending(i => _userData.GetUserData(user, i)?.LastPlayedDate).First();
                    var newestData = _userData.GetUserData(user, newest);
                    if (newestData?.LastPlayedDate is { } localAt && localAt > record.UpdatedAt)
                    {
                        PlaybackProgressService.ApplyLocalState(record, newest.Id,
                            newestData.PlaybackPositionTicks / TimeSpan.TicksPerMillisecond,
                            newest.RunTimeTicks!.Value / TimeSpan.TicksPerMillisecond,
                            newestData.Played, localAt);
                        return Task.FromResult(true);
                    }

                    var item = matches.FirstOrDefault(i => i.Id == record.JellyfinItemId) ?? newest;
                    var currentData = _userData.GetUserData(user, item);
                    // A mark-unwatched reset clears LastPlayedDate, so timestamp ordering alone
                    // cannot detect it. Only adopt a reset of the SAME already-synchronized copy:
                    // an untouched edition or a pending newer stream report must not erase progress.
                    if (record.JellyfinItemId == item.Id && record.AppliedRevision == record.Revision
                        && (record.PositionMs > 0 || record.Completed)
                        && currentData is { Played: false, PlaybackPositionTicks: 0, LastPlayedDate: null })
                    {
                        PlaybackProgressService.ApplyLocalState(record, item.Id, 0,
                            item.RunTimeTicks!.Value / TimeSpan.TicksPerMillisecond, false, DateTime.UtcNow);
                        return Task.FromResult(true);
                    }
                    if (record.AppliedRevision >= record.Revision && record.JellyfinItemId == item.Id)
                    {
                        return Task.FromResult(false);
                    }

                    if (record.DurationMs <= 0)
                    {
                        return Task.FromResult(false);
                    }

                    var data = _userData.GetUserData(user, item);
                    if (data is null)
                    {
                        return Task.FromResult(false);
                    }

                    ApplyToUserData(record, data, item.RunTimeTicks!.Value);
                    _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, token);
                    record.JellyfinItemId = item.Id;
                    return Task.FromResult(true);
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The durable revision remains pending until Jellyfin accepts it.
                _logger.LogWarning(ex, "Orca Engine: could not transfer resume point {ProgressId}.", snapshot.Id);
            }
        }
    }

    // Mirrors the reconcile callback's no-op path: applied to an accessible copy, no newer native
    // playback on any edition, and no mark-unwatched reset of the mapped copy.
    private bool IsSettled(PlaybackProgressRecord record, List<BaseItem> matches, Jellyfin.Database.Implementations.Entities.User user)
    {
        var mapped = matches.FirstOrDefault(i => i.Id == record.JellyfinItemId);
        if (mapped is null || record.AppliedRevision < record.Revision
            || matches.Any(i => _userData.GetUserData(user, i)?.LastPlayedDate > record.UpdatedAt))
        {
            return false;
        }

        return !((record.PositionMs > 0 || record.Completed)
            && _userData.GetUserData(user, mapped) is { Played: false, PlaybackPositionTicks: 0, LastPlayedDate: null });
    }

    private IReadOnlyList<BaseItem> AccessibleItems(Jellyfin.Database.Implementations.Entities.User user)
        => _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = Kinds,
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
        });

    internal PlaybackIdentity? Identify(BaseItem item)
    {
        if (item.GetBaseItemKind() == BaseItemKind.Movie && TmdbId(item) is { } movieId)
        {
            return new PlaybackIdentity(movieId, "movie", 0, 0);
        }

        if (item is Episode episode && episode.ParentIndexNumber is >= 0 && episode.IndexNumber is > 0
            && _library.GetItemById(episode.SeriesId) is { } series && TmdbId(series) is { } seriesId)
        {
            return new PlaybackIdentity(seriesId, "tv", episode.ParentIndexNumber.Value, episode.IndexNumber.Value);
        }

        return null;
    }

    internal static void ApplyToUserData(PlaybackProgressRecord record, UserItemData data, long runtimeTicks)
    {
        var fraction = record.DurationMs > 0 ? Math.Clamp((double)record.PositionMs / record.DurationMs, 0, 1) : 0;
        data.PlaybackPositionTicks = record.Completed ? 0 : (long)Math.Round(fraction * runtimeTicks);
        data.Played = record.Completed;
        data.LastPlayedDate = record.UpdatedAt;
        // Importing progress must not increment play count on every heartbeat or scan.
        if (record.Completed && data.PlayCount == 0)
        {
            data.PlayCount = 1;
        }
    }

    private static int? TmdbId(BaseItem item)
    {
        var raw = item.ProviderIds?.FirstOrDefault(p => p.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)).Value;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.Item is null || e.UserData is null || !IsPlaybackChange(e.SaveReason)
            || Identify(e.Item) is not { } identity || e.Item.RunTimeTicks is not > 0)
        {
            return;
        }

        // Copy the event values before the mutable Jellyfin UserItemData is changed again.
        // Call immediately so the storage gate observes saves in event order.
        _ = ObserveNativeAsync(e.UserId, identity, e.Item.Id,
            e.UserData.PlaybackPositionTicks / TimeSpan.TicksPerMillisecond,
            e.Item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond,
            e.UserData.Played, DateTime.UtcNow);
    }

    internal static bool IsPlaybackChange(UserDataSaveReason reason) => reason is
        UserDataSaveReason.PlaybackStart or UserDataSaveReason.PlaybackProgress
        or UserDataSaveReason.PlaybackFinished or UserDataSaveReason.TogglePlayed;

    private async Task ObserveNativeAsync(Guid userId, PlaybackIdentity identity, Guid itemId,
        long position, long duration, bool completed, DateTime at)
    {
        try
        {
            await _progress.ObserveLocalAsync(userId, identity, itemId, position, duration, completed, at, _stopping.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: could not retain library playback for {ItemId}.", itemId);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                try
                {
                    await ReconcileAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Orca Engine: pending resume transfer will retry.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
