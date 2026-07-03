using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Jellyfin scheduled task that warms the Orca trailer cache: downloads + transcodes trailers for the
/// most prominent library (WatchNow) and requestable (discover) titles so card-focus previews play
/// instantly. Appears under Dashboard → Scheduled Tasks, runs every 60 hours by default, and — like
/// any Jellyfin task — the trigger is fully user-editable there (or run it manually on demand). A
/// cheap no-op when yt-dlp/ffmpeg are absent or everything is already cached.
/// </summary>
public class WarmTrailersTask : IScheduledTask
{
    /// <summary>Titles warmed per pool (library + requestable) per run.</summary>
    private const int PerPool = 60;

    /// <summary>Default interval between runs.</summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(60);

    private readonly ITrailerService _trailers;
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="WarmTrailersTask"/> class.
    /// </summary>
    /// <param name="trailers">The trailer service.</param>
    /// <param name="factory">The database context factory.</param>
    public WarmTrailersTask(ITrailerService trailers, IWholphinDbContextFactory factory)
    {
        _trailers = trailers;
        _factory = factory;
    }

    /// <inheritdoc />
    public string Name => "Warm Orca trailer cache";

    /// <inheritdoc />
    public string Key => "OrcaEngineWarmTrailers";

    /// <inheritdoc />
    public string Description =>
        "Downloads and caches trailers for prominent library and requestable titles so Orca X card-focus previews play instantly.";

    /// <inheritdoc />
    public string Category => "Orca Engine";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_trailers.IsAvailable)
        {
            progress.Report(100);
            return;
        }

        List<(int TmdbId, MediaType MediaType)> items;
        await using (var db = _factory.Create())
        {
            var watchNow = await db.CatalogItems
                .Where(c => c.TmdbId != null && c.Availability == AvailabilityState.WatchNow)
                .OrderByDescending(c => c.CommunityRating)
                .Take(PerPool)
                .Select(c => new { TmdbId = c.TmdbId!.Value, c.MediaType })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var requestable = await db.CatalogItems
                .Where(c => c.TmdbId != null && c.Availability == AvailabilityState.Request)
                .OrderByDescending(c => c.CommunityRating)
                .Take(PerPool)
                .Select(c => new { TmdbId = c.TmdbId!.Value, c.MediaType })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            items = watchNow.Concat(requestable)
                .Select(i => (i.TmdbId, i.MediaType))
                .Distinct()
                .ToList();
        }

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (tmdbId, mediaType) = items[i];
            await _trailers.GetTrailerPathAsync(tmdbId, mediaType, allowDownload: true, cancellationToken).ConfigureAwait(false);
            progress.Report((i + 1) * 100.0 / items.Count);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = DefaultInterval.Ticks,
            },
        };
}
