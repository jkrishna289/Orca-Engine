using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Behavior;

/// <summary>
/// Default <see cref="IWatchHistoryImporter"/>. Walks every Movie, Series and Episode in the library
/// once, reads each user's Jellyfin user data for it, and writes the behavior events that live
/// capture would have produced had the plugin been installed all along.
/// </summary>
/// <remarks>
/// <para>
/// A full scan rather than the cheaper played-only query, deliberately: those queries miss a title
/// the user rated but never finished, and the whole point of this job is that it gets
/// <em>everything</em>. It is a one-time setup action with a progress bar, so the cost is paid once
/// and is visible while it is paid.
/// </para>
/// <para>
/// Episodes roll up to their series — the catalog holds Movies and Series, so an event on an episode
/// id resolves to nothing and teaches the engine nothing.
/// </para>
/// </remarks>
public sealed class WatchHistoryImporter : IWatchHistoryImporter
{
    /// <summary>Marks events this importer wrote, so a re-run can replace exactly its own rows.</summary>
    public const string ImportContext = "{\"source\":\"history-import\"}";

    /// <summary>Alert key for "the import ran and a profile is still not confident".</summary>
    public const string ConfidenceAlertKey = "history.confidence";

    /// <summary>Confidence a profile is expected to reach once its full history is imported.</summary>
    public const double TargetConfidence = 0.80;

    // ponytail: flat caps, not a tuned curve. A rewatched film should outweigh a watched-once film,
    // and a fully-watched 60-episode series should not outweigh it sixtyfold. Revisit if the
    // ranking visibly favours long-running shows.
    private const int MaxPlaysPerTitle = 3;
    private const int MaxEpisodeRollup = 5;

    private const int BatchSize = 500;

    private static readonly BaseItemKind[] ScannedKinds =
        { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode };

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IPersonalizationService _personalization;
    private readonly ITasteProfileService _taste;
    private readonly IEngineAlerts _alerts;
    private readonly ILogger<WatchHistoryImporter> _logger;

    private readonly object _gate = new();
    private int _running;
    private HistoryImportProgress _progress = Idle;

    /// <summary>Initializes a new instance of the <see cref="WatchHistoryImporter"/> class.</summary>
    /// <param name="library">Jellyfin library manager (enumerates items).</param>
    /// <param name="users">Jellyfin user manager (enumerates users).</param>
    /// <param name="userData">Jellyfin user-data manager (played/favorite/rating state).</param>
    /// <param name="factory">Database context factory.</param>
    /// <param name="personalization">Affinity recompute, run per user after their import.</param>
    /// <param name="taste">Taste-profile rebuild, run per user after their import.</param>
    /// <param name="alerts">Sticky health alerts.</param>
    /// <param name="logger">The logger.</param>
    public WatchHistoryImporter(
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        IWholphinDbContextFactory factory,
        IPersonalizationService personalization,
        ITasteProfileService taste,
        IEngineAlerts alerts,
        ILogger<WatchHistoryImporter> logger)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _factory = factory;
        _personalization = personalization;
        _taste = taste;
        _alerts = alerts;
        _logger = logger;
    }

    private static HistoryImportProgress Idle => new(
        false, "idle", null, null, 0, 0, 0, null, Array.Empty<HistoryImportUser>());

    /// <inheritdoc />
    public HistoryImportProgress Progress
    {
        get { lock (_gate) { return _progress; } }
    }

    /// <inheritdoc />
    public bool TryStart()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orca Engine: watch-history import failed.");
                Update(p => p with
                {
                    Running = false,
                    Phase = "failed",
                    FinishedUtc = DateTime.UtcNow,
                    Error = ex.GetType().Name + ": " + ex.Message,
                });
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });

        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;

        // Not IUserManager.Users — that property does not exist on every 10.11 patch. See JellyfinUsers.
        var users = JellyfinUsers.AllIds(_users)
            .Select(id => _users.GetUserById(id))
            .Where(u => u is not null)
            .Select(u => u!)
            .ToList();

        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = ScannedKinds,
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false),
        });

        _logger.LogInformation(
            "Orca Engine: importing watch history for {Users} users across {Items} library items.",
            users.Count,
            items.Count);

        lock (_gate)
        {
            _progress = new HistoryImportProgress(
                Running: true,
                Phase: "starting",
                StartedUtc: started,
                FinishedUtc: null,
                UsersTotal: users.Count,
                UsersDone: 0,
                EventsImported: 0,
                Error: null,
                Users: users
                    .Select(u => new HistoryImportUser(
                        u.Id,
                        string.IsNullOrWhiteSpace(u.Username) ? u.Id.ToString("N") : u.Username,
                        "pending",
                        0,
                        items.Count,
                        0,
                        0,
                        0,
                        null))
                    .ToList());
        }

        // One lookup shared by every user: which Jellyfin ids the engine actually has a catalog row
        // for. An event on anything else is written but cannot teach the profile anything until the
        // library sync catches up, so it is counted and reported rather than silently dropped.
        HashSet<Guid> catalogIds;
        await using (var db = _factory.Create())
        {
            catalogIds = (await db.CatalogItems.AsNoTracking()
                    .Where(c => c.JellyfinItemId != null)
                    .Select(c => c.JellyfinItemId!.Value)
                    .ToListAsync(ct).ConfigureAwait(false))
                .ToHashSet();
        }

        var totalEvents = 0;

        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i];
            var name = string.IsNullOrWhiteSpace(user.Username) ? user.Id.ToString("N") : user.Username;
            SetUser(i, u => u with { State = "running" });
            Update(p => p with { Phase = "scanning " + name });

            try
            {
                var result = await ImportUserAsync(user, items, catalogIds, i, ct).ConfigureAwait(false);
                totalEvents += result.Events;

                Update(p => p with
                {
                    Phase = "rebuilding profile for " + name,
                    EventsImported = totalEvents,
                });

                // Recomputed inline rather than through the debounced queue: the operator is
                // watching a progress bar, and the confidence number is the answer they came for.
                var affinity = await _personalization.RecomputeAsync(user.Id, ct).ConfigureAwait(false);
                await _taste.RebuildAsync(user.Id, ct).ConfigureAwait(false);

                SetUser(i, u => u with
                {
                    State = "done",
                    ItemsScanned = items.Count,
                    EventsImported = result.Events,
                    Unresolved = result.Unresolved,
                    Confidence = Math.Round(affinity.Confidence, 3),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orca Engine: watch-history import failed for user {User}.", name);
                SetUser(i, u => u with { State = "failed", Error = ex.GetType().Name + ": " + ex.Message });
            }

            Update(p => p with { UsersDone = p.UsersDone + 1 });
        }

        Update(p => p with
        {
            Running = false,
            Phase = "complete",
            FinishedUtc = DateTime.UtcNow,
            EventsImported = totalEvents,
        });

        RaiseOutcomeAlerts();
        _logger.LogInformation(
            "Orca Engine: watch-history import complete ({Events} events).",
            totalEvents);
    }

    /// <summary>
    /// Imports one user: scans every item, rolls episodes up to their series, and replaces this
    /// importer's previous rows for the user.
    /// </summary>
    private async Task<(int Events, int Unresolved)> ImportUserAsync(
        Jellyfin.Database.Implementations.Entities.User user,
        IReadOnlyList<BaseItem> items,
        HashSet<Guid> catalogIds,
        int index,
        CancellationToken ct)
    {
        var events = new List<BehaviorEvent>();
        var series = new Dictionary<Guid, SeriesTally>();
        var unresolved = 0;

        for (var scanned = 0; scanned < items.Count; scanned++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[scanned];
            var data = _userData.GetUserData(user, item);
            if (data is null || !HasSignal(data))
            {
                continue;
            }

            if (item is Episode episode)
            {
                // The episode is never a catalog row; everything it knows belongs to the series.
                if (episode.SeriesId != Guid.Empty)
                {
                    Tally(series, episode.SeriesId).Absorb(data);
                }
            }
            else if (item is Series show)
            {
                // A series can carry its own favorite/rating with no episode ever played.
                Tally(series, show.Id).Absorb(data);
            }
            else
            {
                if (!catalogIds.Contains(item.Id))
                {
                    unresolved++;
                }

                AppendMovie(events, user.Id, item.Id, data);
            }

            // Reporting every item would be one lock per item across tens of thousands of items.
            if ((scanned & 0xFF) == 0)
            {
                var done = scanned;
                SetUser(index, u => u with { ItemsScanned = done });
            }
        }

        foreach (var (seriesId, tally) in series)
        {
            if (!catalogIds.Contains(seriesId))
            {
                unresolved++;
            }

            tally.Append(events, user.Id, seriesId);
        }

        await using var db = _factory.Create();

        // Idempotency: drop only what a previous run of THIS importer wrote. Live-captured events
        // carry a device/client context and are never matched by this filter.
        await db.BehaviorEvents
            .Where(e => e.UserId == user.Id && e.ContextJson == ImportContext)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        for (var offset = 0; offset < events.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            db.BehaviorEvents.AddRange(events.Skip(offset).Take(BatchSize));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }

        return (events.Count, unresolved);
    }

    /// <summary>Whether Jellyfin holds anything about this item worth importing.</summary>
    /// <param name="data">The Jellyfin user data for one item.</param>
    /// <returns><c>true</c> when there is a signal to import.</returns>
    public static bool HasSignal(UserItemData data) =>
        data.Played || data.IsFavorite || data.Rating.HasValue || data.PlayCount > 0;

    /// <summary>
    /// Synthesizes the events for one directly-catalogued title (a movie).
    /// </summary>
    /// <param name="events">The list to append to.</param>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="data">The user data to convert.</param>
    public static void AppendMovie(List<BehaviorEvent> events, Guid userId, Guid itemId, UserItemData data)
    {
        var at = data.LastPlayedDate?.ToUniversalTime();

        if (data.Played || data.PlayCount > 0)
        {
            // No play date means it was marked played, not observed being played — a weaker claim,
            // and the weaker signal type says so rather than inventing a timestamp at full weight.
            var plays = Math.Clamp(Math.Max(data.PlayCount, 1), 1, MaxPlaysPerTitle);
            for (var i = 0; i < plays; i++)
            {
                events.Add(Event(
                    userId,
                    itemId,
                    at is null ? BehaviorEventType.MarkedPlayed : BehaviorEventType.PlaybackCompleted,
                    1.0,
                    at));
            }
        }

        if (data.IsFavorite)
        {
            events.Add(Event(userId, itemId, BehaviorEventType.MarkedFavorite, 1.0, at));
        }

        if (data.Rating is { } rating)
        {
            events.Add(Event(userId, itemId, BehaviorEventType.Rated, rating, at));
        }
    }

    private static SeriesTally Tally(Dictionary<Guid, SeriesTally> map, Guid seriesId)
    {
        if (!map.TryGetValue(seriesId, out var tally))
        {
            tally = new SeriesTally();
            map[seriesId] = tally;
        }

        return tally;
    }

    private static BehaviorEvent Event(Guid userId, Guid itemId, BehaviorEventType type, double value, DateTime? at) => new()
    {
        UserId = userId,
        JellyfinItemId = itemId,
        EventType = type,
        Value = value,
        Timestamp = at ?? DateTime.UtcNow,
        ContextJson = ImportContext,
    };

    private void RaiseOutcomeAlerts()
    {
        var progress = Progress;

        var weak = progress.Users.Where(u => u.State == "done" && u.Confidence < TargetConfidence).ToList();
        if (weak.Count > 0)
        {
            _alerts.Raise(
                ConfidenceAlertKey,
                "warn",
                weak.Count + " profile(s) are still below "
                    + TargetConfidence.ToString("P0", CultureInfo.InvariantCulture)
                    + " confidence after importing history.",
                "Below target: "
                + string.Join(", ", weak.Select(u => u.UserName + " (" + u.Confidence.ToString("P0", CultureInfo.InvariantCulture) + ")"))
                + ". Either the account genuinely has very little watch history, or its watched titles have no "
                + "matching catalog row yet — check the unresolved column and run a library sync first.");
        }
        else
        {
            _alerts.Clear(ConfidenceAlertKey);
        }
    }

    private void Update(Func<HistoryImportProgress, HistoryImportProgress> change)
    {
        lock (_gate)
        {
            _progress = change(_progress);
        }
    }

    private void SetUser(int index, Func<HistoryImportUser, HistoryImportUser> change)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _progress.Users.Count)
            {
                return;
            }

            var users = _progress.Users.ToList();
            users[index] = change(users[index]);
            _progress = _progress with { Users = users };
        }
    }

    /// <summary>
    /// Everything one series accumulated across its episodes, flushed into events once. Watched
    /// episodes drive the count; favorite and rating collapse to a single signal each so a series
    /// cannot out-shout a film purely by having more parts.
    /// </summary>
    public sealed class SeriesTally
    {
        private int _watched;
        private bool _favorite;
        private double _ratingSum;
        private int _ratingCount;
        private DateTime? _lastPlayed;

        /// <summary>Folds one episode's (or the series' own) user data into the tally.</summary>
        /// <param name="data">The Jellyfin user data.</param>
        public void Absorb(UserItemData data)
        {
            if (data.Played || data.PlayCount > 0)
            {
                _watched++;
            }

            _favorite |= data.IsFavorite;

            if (data.Rating is { } rating)
            {
                _ratingSum += rating;
                _ratingCount++;
            }

            if (data.LastPlayedDate is { } played)
            {
                var utc = played.ToUniversalTime();
                if (_lastPlayed is null || utc > _lastPlayed)
                {
                    _lastPlayed = utc;
                }
            }
        }

        /// <summary>Flushes the tally into events attributed to the series.</summary>
        /// <param name="events">The list to append to.</param>
        /// <param name="userId">The Jellyfin user id.</param>
        /// <param name="seriesId">The series' Jellyfin item id.</param>
        public void Append(List<BehaviorEvent> events, Guid userId, Guid seriesId)
        {
            var plays = Math.Min(_watched, MaxEpisodeRollup);
            for (var i = 0; i < plays; i++)
            {
                events.Add(Event(
                    userId,
                    seriesId,
                    _lastPlayed is null ? BehaviorEventType.MarkedPlayed : BehaviorEventType.PlaybackCompleted,
                    1.0,
                    _lastPlayed));
            }

            if (_favorite)
            {
                events.Add(Event(userId, seriesId, BehaviorEventType.MarkedFavorite, 1.0, _lastPlayed));
            }

            if (_ratingCount > 0)
            {
                events.Add(Event(userId, seriesId, BehaviorEventType.Rated, _ratingSum / _ratingCount, _lastPlayed));
            }
        }
    }
}
