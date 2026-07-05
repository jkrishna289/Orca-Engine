using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Periodically warms the trailer cache with the titles users are most likely to watch next, using
/// the multi-signal <see cref="ITrailerPredictionService"/> (Continue Watching + recent engagement +
/// interest + popularity) rather than raw community rating. Gated on the <c>TrailerPrebuffer</c> flag
/// and the presence of yt-dlp/ffmpeg — a complete no-op when either is off/absent. Warmed titles are
/// enqueued at background priority so they never compete with user-visible focus requests.
/// </summary>
public class TrailerPrebufferWorker : IHostedService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(2);

    /// <summary>How many predicted titles to warm per cycle.</summary>
    private const int PrebufferCount = 48;

    private readonly ITrailerService _trailers;
    private readonly ITrailerQueue _queue;
    private readonly ITrailerPredictionService _prediction;
    private readonly ISettingsService _settings;
    private readonly ILogger<TrailerPrebufferWorker> _logger;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerPrebufferWorker"/> class.
    /// </summary>
    public TrailerPrebufferWorker(
        ITrailerService trailers,
        ITrailerQueue queue,
        ITrailerPredictionService prediction,
        ISettingsService settings,
        ILogger<TrailerPrebufferWorker> logger)
    {
        _trailers = trailers;
        _queue = queue;
        _prediction = prediction;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(InitialDelay, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    if (_trailers.IsAvailable)
                    {
                        var settings = await _settings.ResolveAsync(null, ct).ConfigureAwait(false);
                        if (settings.Features.TrailerPrebuffer)
                        {
                            await PrebufferAsync(ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Orca Engine: trailer pre-buffer cycle failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task PrebufferAsync(CancellationToken ct)
    {
        // Predicted most-likely-next titles (falls back to top-rated on a cold start / failure).
        var items = await _prediction.GetWarmCandidatesAsync(PrebufferCount, ct).ConfigureAwait(false);

        // Enqueue at background priority; the bounded queue coalesces already-cached/in-flight titles
        // and drains highest-priority-first, so this never competes with user-visible focus requests.
        foreach (var (tmdbId, mediaType) in items)
        {
            _queue.Enqueue(tmdbId, mediaType, TrailerPriority.BackgroundWorker);
        }

        if (items.Count > 0)
        {
            _logger.LogDebug("Orca Engine: enqueued {Count} predicted trailers for background warming.", items.Count);
        }
    }
}
