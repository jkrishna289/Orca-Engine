using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Wholphin.Engine.Intelligence;

/// <summary>
/// Default <see cref="ISeriesUserStateService"/>. Walks a series' available episodes through the
/// Jellyfin library + user-data managers, tallies watched-per-season, and hands the tallies to the
/// pure <see cref="SeriesStateCalculator"/>. Season 0 (specials) is excluded by the calculator.
/// </summary>
public sealed class SeriesUserStateService : ISeriesUserStateService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>Initializes a new instance of the <see cref="SeriesUserStateService"/> class.</summary>
    /// <param name="libraryManager">Jellyfin library manager (enumerates episodes).</param>
    /// <param name="userManager">Jellyfin user manager (resolves the user entity).</param>
    /// <param name="userDataManager">Jellyfin user-data manager (per-episode played state).</param>
    public SeriesUserStateService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <inheritdoc />
    public SeriesStateResult? GetState(Guid userId, Guid seriesJellyfinId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null || seriesJellyfinId == Guid.Empty || _libraryManager.GetItemById(seriesJellyfinId) is null)
        {
            return null;
        }

        var query = new InternalItemsQuery(user)
        {
            AncestorIds = new[] { seriesJellyfinId },
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
        };

        // season number -> [total episodes, watched episodes]
        var perSeason = new Dictionary<int, int[]>();
        DateTime? lastActivity = null;

        foreach (var item in _libraryManager.GetItemList(query))
        {
            var season = item.ParentIndexNumber ?? 0;
            if (!perSeason.TryGetValue(season, out var tally))
            {
                tally = new int[2];
                perSeason[season] = tally;
            }

            tally[0]++;

            var data = _userDataManager.GetUserData(user, item);
            if (data?.Played == true)
            {
                tally[1]++;
                if (data.LastPlayedDate is { } played && (lastActivity is null || played > lastActivity))
                {
                    lastActivity = played;
                }
            }
        }

        if (perSeason.Count == 0)
        {
            return null;
        }

        var seasons = perSeason
            .Select(kv => new SeasonProgress(kv.Key, kv.Value[0], kv.Value[1]))
            .ToList();

        return SeriesStateCalculator.Derive(
            seasons,
            lastActivity is { } activity ? ToUtc(activity) : null,
            DateTime.UtcNow);
    }

    /// <inheritdoc />
    public IReadOnlyList<StartedSeries> GetStartedSeries(Guid userId, int max, CancellationToken ct = default)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null || max <= 0)
        {
            return Array.Empty<StartedSeries>();
        }

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsPlayed = true,
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
        };

        // A series can have many played episodes; keep only the latest activity per series.
        var latest = new Dictionary<Guid, DateTime>();
        foreach (var item in _libraryManager.GetItemList(query))
        {
            ct.ThrowIfCancellationRequested();
            if (item is not Episode episode || episode.SeriesId == Guid.Empty)
            {
                continue;
            }

            var played = _userDataManager.GetUserData(user, item)?.LastPlayedDate ?? DateTime.MinValue;
            if (!latest.TryGetValue(episode.SeriesId, out var current) || played > current)
            {
                latest[episode.SeriesId] = played;
            }
        }

        return latest
            .OrderByDescending(kv => kv.Value)
            .Take(max)
            .Select(kv => new StartedSeries(kv.Key, ToUtc(kv.Value)))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlySet<Guid> GetResumingSeriesIds(Guid userId, CancellationToken ct = default)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return new HashSet<Guid>();
        }

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsResumable = true,
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
        };

        var ids = new HashSet<Guid>();
        foreach (var item in _libraryManager.GetItemList(query))
        {
            ct.ThrowIfCancellationRequested();
            if (item is Episode episode && episode.SeriesId != Guid.Empty)
            {
                ids.Add(episode.SeriesId);
            }
        }

        return ids;
    }

    private static DateTime ToUtc(DateTime value)
        => value == DateTime.MinValue ? DateTime.MinValue : value.ToUniversalTime();
}
