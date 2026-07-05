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
using Wholphin.Engine.Integrations.Arr;
using Wholphin.Engine.Intelligence;
using Wholphin.Engine.Settings;
using MediaType = Wholphin.Engine.Data.Enums.MediaType;

namespace Wholphin.Engine.Home;

/// <summary>
/// Default <see cref="INewSinceProvider"/>. Emits <em>strict release/availability events</em> since
/// the user's last visit — never backlog. Three fail-soft sources, each keyed off the last-seen time:
/// <list type="bullet">
///   <item><description>movies that became available in the library (catalog <c>DateAdded</c>);</description></item>
///   <item><description>series whose episode <b>aired</b> in the window (Jellyfin premiere date);</description></item>
///   <item><description>episodes/movies that <b>downloaded</b> in the window (*arr import history).</description></item>
/// </list>
/// A series that merely had an old season added to the library is intentionally NOT surfaced here —
/// that is a user-state concern handled by "Continue the Story".
/// </summary>
public class NewSinceProvider : INewSinceProvider
{
    /// <summary>Roaming-settings key holding the user's last-seen timestamp (ISO-8601 UTC).</summary>
    public const string LastSeenKey = "meta.lastSeenUtc";

    /// <summary>When a user has no last-seen yet, look back this far so the row isn't empty on first run.</summary>
    private static readonly TimeSpan FirstRunWindow = TimeSpan.FromDays(14);

    /// <summary>Don't surface items older than this even if last-seen is very old (keeps the row "fresh").</summary>
    private static readonly TimeSpan MaxLookback = TimeSpan.FromDays(45);

    private readonly IWholphinDbContextFactory _factory;
    private readonly IUserSettingsStore _userSettings;
    private readonly ISeriesIntelligenceEngine _engine;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<NewSinceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewSinceProvider"/> class.
    /// </summary>
    public NewSinceProvider(
        IWholphinDbContextFactory factory,
        IUserSettingsStore userSettings,
        ISeriesIntelligenceEngine engine,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<NewSinceProvider> logger)
    {
        _factory = factory;
        _userSettings = userSettings;
        _engine = engine;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<NewSinceResult> GetAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || limit <= 0)
        {
            return NewSinceResult.Empty;
        }

        var since = await ReadLastSeenAsync(userId, ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var floor = now - MaxLookback;
        if (since < floor)
        {
            since = floor;
        }

        await using var db = _factory.Create();

        // catalog id -> (event time used for ordering, "what changed" blurb). First writer wins, so
        // the source order below encodes reason priority (aired episode > added movie > downloaded).
        var events = new Dictionary<long, (DateTime Key, string Reason)>();
        var items = new Dictionary<long, CatalogItem>();

        void Add(CatalogItem item, DateTime key, string reason)
        {
            if (!events.ContainsKey(item.Id))
            {
                events[item.Id] = (key, reason);
                items[item.Id] = item;
            }
        }

        // 1. Series whose episode AIRED in the window (the canonical "new episode" event).
        foreach (var (item, key, reason) in await GetAiredEpisodesAsync(db, userId, since, now, ct).ConfigureAwait(false))
        {
            Add(item, key, reason);
        }

        // 2. Movies that became available in the library since the cutoff.
        var newMovies = await db.CatalogItems
            .Where(c => c.MediaType == MediaType.Movie && c.DateAdded != null && c.DateAdded > since)
            .OrderByDescending(c => c.DateAdded)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var movie in newMovies)
        {
            Add(movie, movie.DateAdded ?? now, "New in your library");
        }

        // 3. *arr: episodes/movies that DOWNLOADED (became watchable) in the window.
        foreach (var (item, key, reason) in await GetDownloadedAsync(db, since, ct).ConfigureAwait(false))
        {
            Add(item, key, reason);
        }

        var ordered = events
            .OrderByDescending(kv => kv.Value.Key)
            .Take(limit)
            .ToList();

        var resultItems = ordered.Select(kv => items[kv.Key]).ToList();
        var reasons = ordered.ToDictionary(kv => kv.Key, kv => kv.Value.Reason);
        return new NewSinceResult(resultItems, reasons);
    }

    /// <inheritdoc />
    public Task MarkSeenAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        var nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        return _userSettings.SetAsync(userId, LastSeenKey, nowIso, ct);
    }

    /// <summary>Series whose most-recent episode aired in the window, mapped to their catalog rows.</summary>
    private async Task<IReadOnlyList<(CatalogItem Item, DateTime Key, string Reason)>> GetAiredEpisodesAsync(
        WholphinDbContext db, Guid userId, DateTime since, DateTime now, CancellationToken ct)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Array.Empty<(CatalogItem, DateTime, string)>();
        }

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            MinPremiereDate = since,
            MaxPremiereDate = now,
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
        };

        // Keep only the newest aired episode per series.
        var newestPerSeries = new Dictionary<Guid, (DateTime Premiere, int? Season, int? Number)>();
        foreach (var item in _libraryManager.GetItemList(query))
        {
            ct.ThrowIfCancellationRequested();
            if (item is not Episode episode || episode.SeriesId == Guid.Empty || item.PremiereDate is not { } premiere)
            {
                continue;
            }

            var premiereUtc = premiere.ToUniversalTime();
            if (!newestPerSeries.TryGetValue(episode.SeriesId, out var current) || premiereUtc > current.Premiere)
            {
                newestPerSeries[episode.SeriesId] = (premiereUtc, item.ParentIndexNumber, item.IndexNumber);
            }
        }

        if (newestPerSeries.Count == 0)
        {
            return Array.Empty<(CatalogItem, DateTime, string)>();
        }

        var seriesIds = newestPerSeries.Keys.ToList();
        var catalog = await db.CatalogItems
            .Where(c => c.JellyfinItemId != null && seriesIds.Contains(c.JellyfinItemId.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<(CatalogItem, DateTime, string)>();
        foreach (var item in catalog)
        {
            if (item.JellyfinItemId is { } jf && newestPerSeries.TryGetValue(jf, out var ep))
            {
                result.Add((item, ep.Premiere, EpisodeReason("New episode", ep.Season, ep.Number)));
            }
        }

        return result;
    }

    /// <summary>*arr import events in the window, mapped by TMDB id to catalog rows.</summary>
    private async Task<IReadOnlyList<(CatalogItem Item, DateTime Key, string Reason)>> GetDownloadedAsync(
        WholphinDbContext db, DateTime since, CancellationToken ct)
    {
        IReadOnlyList<ArrHistoryEvent> history;
        try
        {
            history = await _engine.GetReleaseEventsAsync(since, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: *arr history lookup failed for New Since Away.");
            return Array.Empty<(CatalogItem, DateTime, string)>();
        }

        if (history.Count == 0)
        {
            return Array.Empty<(CatalogItem, DateTime, string)>();
        }

        // Newest import per TMDB id.
        var newestPerTmdb = new Dictionary<int, ArrHistoryEvent>();
        foreach (var ev in history)
        {
            if (ev.TmdbId is not { } tmdb || tmdb <= 0)
            {
                continue;
            }

            if (!newestPerTmdb.TryGetValue(tmdb, out var current) || ev.OccurredUtc > current.OccurredUtc)
            {
                newestPerTmdb[tmdb] = ev;
            }
        }

        if (newestPerTmdb.Count == 0)
        {
            return Array.Empty<(CatalogItem, DateTime, string)>();
        }

        var tmdbIds = newestPerTmdb.Keys.ToList();
        var catalog = await db.CatalogItems
            .Where(c => c.TmdbId != null && tmdbIds.Contains(c.TmdbId.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<(CatalogItem, DateTime, string)>();
        foreach (var item in catalog)
        {
            if (item.TmdbId is { } tmdb && newestPerTmdb.TryGetValue(tmdb, out var ev))
            {
                var reason = ev.MediaType == MediaType.Series
                    ? EpisodeReason("New episode downloaded", ev.SeasonNumber, ev.EpisodeNumber)
                    : "Just downloaded";
                result.Add((item, ev.OccurredUtc, reason));
            }
        }

        return result;
    }

    private static string EpisodeReason(string prefix, int? season, int? number)
        => season is { } s && number is { } n
            ? string.Format(CultureInfo.InvariantCulture, "{0} · S{1}:E{2}", prefix, s, n)
            : prefix;

    private async Task<DateTime> ReadLastSeenAsync(Guid userId, CancellationToken ct)
    {
        var raw = await _userSettings.GetAsync(userId, LastSeenKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(raw.Trim().Trim('"'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return DateTime.UtcNow - FirstRunWindow;
    }
}
