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
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Playback;
using Wholphin.Engine.Sync;

namespace Wholphin.Engine.Home;

/// <summary>
/// Resolves "Continue Watching" items from Jellyfin's per-user resume points (movies + episodes),
/// projecting each into the engine's catalog shape so the card selector can render it like any
/// other item. Durable streaming history is merged by identity with live library history.
/// </summary>
public sealed class JellyfinResumeProvider : IResumeProvider
{
    private static readonly BaseItemKind[] ResumableKinds = { BaseItemKind.Movie, BaseItemKind.Episode };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaybackProgressService _progress;
    private readonly JellyfinProgressBridge _bridge;
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinResumeProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager (queries resumable items).</param>
    /// <param name="userManager">Jellyfin user manager (resolves the user entity).</param>
    /// <param name="userDataManager">Jellyfin user-data manager (per-item resume position).</param>
    public JellyfinResumeProvider(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IPlaybackProgressService progress,
        JellyfinProgressBridge bridge,
        IWholphinDbContextFactory factory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _progress = progress;
        _bridge = bridge;
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResumePoint>> GetResumeAsync(Guid userId, int limit, CancellationToken ct)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null || limit <= 0)
        {
            return Array.Empty<ResumePoint>();
        }

        await _bridge.ReconcileUserAsync(userId, ct).ConfigureAwait(false);
        var stored = await _progress.GetRecentAsync(userId, int.MaxValue, ct).ConfigureAwait(false);
        var records = stored.ToDictionary(p => new PlaybackIdentity(p.TmdbId, p.MediaType, p.Season, p.Episode));
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = ResumableKinds,
            // The loop below skips position <= 0 anyway; let Jellyfin filter instead of walking the library.
            IsResumable = true,
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
        };

        var scored = new Dictionary<string, (ResumePoint Point, DateTime LastPlayed)>();
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
            var identity = _bridge.Identify(item);
            var record = identity is not null ? records.GetValueOrDefault(identity) : null;
            if (position <= 0 || data?.Played == true ||
                (record is not null && record.UpdatedAt >= (data?.LastPlayedDate ?? DateTime.MinValue)
                    && (record.Completed || record.PositionMs <= 0 || record.AppliedRevision < record.Revision)))
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

            var key = identity?.ToString() ?? item.Id.ToString();
            var lastPlayed = data?.LastPlayedDate ?? DateTime.MinValue;
            if (!scored.TryGetValue(key, out var previous) || previous.LastPlayed < lastPlayed)
            {
                scored[key] = (new ResumePoint(mapped, progress, subtitle), lastPlayed);
            }
        }

        // Stream cards use the ordinary TMDB details route, which can resume streaming and
        // later hand off to the local item. Episode streams wait for episode-aware navigation.
        var streams = stored.Where(p => p.MediaType == "movie" && !p.Completed && p.PositionMs > 0 && p.DurationMs > 0).ToList();
        var tmdbIds = streams.Select(p => p.TmdbId).Distinct().ToArray();
        await using var db = _factory.Create();
        var metadata = await db.CatalogItems.AsNoTracking()
            .Where(p => p.MediaType == Wholphin.Engine.Data.Enums.MediaType.Movie && p.TmdbId != null && tmdbIds.Contains(p.TmdbId.Value))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var record in streams)
        {
            var key = new PlaybackIdentity(record.TmdbId, record.MediaType, record.Season, record.Episode).ToString();
            if (scored.TryGetValue(key, out var native) && native.LastPlayed >= record.UpdatedAt)
            {
                continue;
            }

            var info = metadata.Where(p => p.TmdbId == record.TmdbId).OrderByDescending(p => p.LastSyncedAt).FirstOrDefault();
            var item = new CatalogItem
            {
                TmdbId = record.TmdbId,
                MediaType = Wholphin.Engine.Data.Enums.MediaType.Movie,
                // Not in the library, but out — the viewer is partway through it. Unavailable read
                // "Coming Soon" on the card.
                Availability = info?.Availability ?? AvailabilityState.Request,
                Title = info?.Title ?? record.Title ?? $"Movie {record.TmdbId}",
                ProductionYear = info?.ProductionYear ?? record.Year,
                PosterImageUrl = info?.PosterImageUrl,
                BackdropImageUrl = info?.BackdropImageUrl,
                LogoImageUrl = info?.LogoImageUrl,
                Overview = info?.Overview,
            };
            scored[key] = (new ResumePoint(item, Math.Clamp((double)record.PositionMs / record.DurationMs, 0, 1), null), record.UpdatedAt);
        }

        var ordered = scored.Values
            .OrderByDescending(r => r.LastPlayed)
            .Take(limit)
            .Select(r => r.Point)
            .ToList();

        return ordered;
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
