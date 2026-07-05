using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Jellyfin scheduled task that warms the Orca trailer cache with the titles users are most likely to
/// watch — ranked by the multi-signal <see cref="ITrailerPredictionService"/> (Continue Watching +
/// recent engagement + interest + popularity), not raw rating. Appears under Dashboard → Scheduled
/// Tasks, runs every 60 hours by default (trigger fully user-editable there, or run on demand), and
/// hands the batch to the bounded priority queue at scheduled-warming priority so it never competes
/// with user-visible trailer requests. A cheap no-op when yt-dlp/ffmpeg are absent.
/// </summary>
public class WarmTrailersTask : IScheduledTask
{
    /// <summary>How many predicted titles to warm per run (a broad scheduled net).</summary>
    private const int WarmCount = 120;

    /// <summary>Default interval between runs.</summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(60);

    private readonly ITrailerService _trailers;
    private readonly ITrailerQueue _queue;
    private readonly ITrailerPredictionService _prediction;

    /// <summary>
    /// Initializes a new instance of the <see cref="WarmTrailersTask"/> class.
    /// </summary>
    /// <param name="trailers">The trailer service.</param>
    /// <param name="queue">The trailer priority queue.</param>
    /// <param name="prediction">The predictive warm-candidate ranker.</param>
    public WarmTrailersTask(ITrailerService trailers, ITrailerQueue queue, ITrailerPredictionService prediction)
    {
        _trailers = trailers;
        _queue = queue;
        _prediction = prediction;
    }

    /// <inheritdoc />
    public string Name => "Warm Orca trailer cache";

    /// <inheritdoc />
    public string Key => "OrcaEngineWarmTrailers";

    /// <inheritdoc />
    public string Description =>
        "Warms trailers for the titles users are most likely to watch (predictive) so Orca X card-focus previews play instantly.";

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

        var items = await _prediction.GetWarmCandidatesAsync(WarmCount, cancellationToken).ConfigureAwait(false);

        // Hand the batch to the bounded priority queue; it dedupes and drains in the background. The
        // task completes as soon as the batch is enqueued (production continues asynchronously).
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (tmdbId, mediaType) = items[i];
            _queue.Enqueue(tmdbId, mediaType, TrailerPriority.ScheduledWarming);
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
