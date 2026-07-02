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
using Wholphin.Engine.Sync;

namespace Wholphin.Engine.Home;

/// <summary>
/// Resolves "Continue Watching" items from Jellyfin's per-user resume points (movies + episodes),
/// projecting each into the engine's catalog shape so the card selector can render it like any
/// other item. Resume state lives in Jellyfin's user data, not the engine catalog, so this reads
/// it live (the home read-model that consumes it is itself cached/precomputed).
/// </summary>
public sealed class JellyfinResumeProvider : IResumeProvider
{
    private static readonly BaseItemKind[] ResumableKinds = { BaseItemKind.Movie, BaseItemKind.Episode };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinResumeProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager (queries resumable items).</param>
    /// <param name="userManager">Jellyfin user manager (resolves the user entity).</param>
    /// <param name="userDataManager">Jellyfin user-data manager (per-item resume position).</param>
    public JellyfinResumeProvider(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ResumePoint>> GetResumeAsync(Guid userId, int limit, CancellationToken ct)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<ResumePoint>>(Array.Empty<ResumePoint>());
        }

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = ResumableKinds,
            IsResumable = true,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
        };

        var scored = new List<(ResumePoint Point, DateTime LastPlayed)>();
        foreach (var item in _libraryManager.GetItemList(query))
        {
            ct.ThrowIfCancellationRequested();

            var runtime = item.RunTimeTicks ?? 0;
            if (runtime <= 0)
            {
                continue;
            }

            var data = _userDataManager.GetUserData(user, item);
            var position = data?.PlaybackPositionTicks ?? 0;
            if (position <= 0)
            {
                continue;
            }

            var progress = Math.Clamp((double)position / runtime, 0, 1);
            var mapped = CatalogMapper.Map(item);
            string? subtitle = null;

            if (item is Episode episode)
            {
                // Surface the series on the card; keep the episode label as the subtitle.
                if (!string.IsNullOrWhiteSpace(episode.SeriesName))
                {
                    mapped.Title = episode.SeriesName;
                }

                subtitle = FormatEpisode(episode);
            }

            scored.Add((new ResumePoint(mapped, progress, subtitle), data?.LastPlayedDate ?? DateTime.MinValue));
        }

        var ordered = scored
            .OrderByDescending(r => r.LastPlayed)
            .Take(limit)
            .Select(r => r.Point)
            .ToList();

        return Task.FromResult<IReadOnlyList<ResumePoint>>(ordered);
    }

    private static string? FormatEpisode(Episode episode)
    {
        var label = episode.ParentIndexNumber is { } season && episode.IndexNumber is { } number
            ? string.Format(CultureInfo.InvariantCulture, "S{0}:E{1}", season, number)
            : null;

        if (string.IsNullOrWhiteSpace(episode.Name))
        {
            return label;
        }

        return label is null ? episode.Name : $"{label} · {episode.Name}";
    }
}
