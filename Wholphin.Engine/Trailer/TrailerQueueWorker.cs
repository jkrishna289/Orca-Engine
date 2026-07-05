using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// The single trailer priority queue plus its bounded worker pool. One instance serves both the
/// <see cref="ITrailerQueue"/> port (producers enqueue here) and the <see cref="IHostedService"/>
/// lifecycle (a fixed number of workers drain it). Workers pick the highest-priority waiting title,
/// with queue aging so long-waiting low-priority items are eventually promoted — background warming
/// is never starved by a steady trickle of focus events. Because only <see cref="MaxConcurrency"/>
/// workers ever run, the host can never be overwhelmed by concurrent yt-dlp/ffmpeg processes (the
/// core defect of the old per-request design).
/// </summary>
public class TrailerQueueWorker : ITrailerQueue, IHostedService
{
    /// <summary>
    /// Maximum concurrent trailer downloads/transcodes. Deliberately small: yt-dlp + ffmpeg are
    /// CPU/IO heavy and share the host with real playback transcodes. This is the single knob that
    /// bounds trailer load; a value of 2 keeps the pipeline moving without starving the server.
    /// </summary>
    private const int MaxConcurrency = 2;

    /// <summary>Every this many seconds a waiting item's effective priority improves by one tier (anti-starvation).</summary>
    private const long AgingStepSeconds = 30;

    /// <summary>Hard cap on the waiting queue; the lowest-priority waiter is dropped when exceeded.</summary>
    private const int MaxQueueDepth = 500;

    private readonly ITrailerService _trailers;
    private readonly ITrailerStateStore _state;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<TrailerQueueWorker> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<TrailerKey, PendingJob> _pending = new();
    private readonly HashSet<TrailerKey> _inFlight = new();
    private readonly SemaphoreSlim _signal = new(0);

    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();

    /// <summary>Initializes a new instance of the <see cref="TrailerQueueWorker"/> class.</summary>
    public TrailerQueueWorker(
        ITrailerService trailers,
        ITrailerStateStore state,
        IEngineMetrics metrics,
        ILogger<TrailerQueueWorker> logger)
    {
        _trailers = trailers;
        _state = state;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Depth
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool IsQueued(int tmdbId, MediaType mediaType)
    {
        var key = new TrailerKey(tmdbId, mediaType);
        lock (_gate)
        {
            return _pending.ContainsKey(key);
        }
    }

    /// <inheritdoc />
    public void Enqueue(int tmdbId, MediaType mediaType, TrailerPriority priority)
    {
        if (tmdbId <= 0 || mediaType is not (MediaType.Movie or MediaType.Series) || !_trailers.IsAvailable)
        {
            return;
        }

        var key = new TrailerKey(tmdbId, mediaType);
        var enqueue = false;
        lock (_gate)
        {
            if (_inFlight.Contains(key))
            {
                // Already being produced — nothing to do.
                return;
            }

            if (_pending.TryGetValue(key, out var existing))
            {
                // Coalesce: keep the most urgent priority, preserve the original wait start (for aging).
                if (priority < existing.Priority)
                {
                    _pending[key] = existing with { Priority = priority };
                }

                return;
            }

            if (_pending.Count >= MaxQueueDepth && !TryEvictLowest(priority))
            {
                // Queue is full of equal-or-higher-priority work; drop this request.
                _metrics.Increment("trailer.queue.dropped");
                return;
            }

            _pending[key] = new PendingJob(priority, DateTime.UtcNow.Ticks);
            enqueue = true;
        }

        if (enqueue)
        {
            _metrics.Increment("trailer.queue.enqueued");
            _signal.Release();
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < MaxConcurrency; i++)
        {
            _workers.Add(Task.Run(() => WorkerLoopAsync(_cts.Token), CancellationToken.None));
        }

        _logger.LogInformation("Orca Engine: trailer queue started with {Workers} workers.", MaxConcurrency);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);

                if (!TryTakeBest(out var key))
                {
                    // Defensive: a permit without a takeable item shouldn't happen, but never spin.
                    continue;
                }

                try
                {
                    await _trailers.ProcessAsync(key.TmdbId, key.MediaType, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Orca Engine: trailer job failed for tmdb {TmdbId}.", key.TmdbId);
                }
                finally
                {
                    lock (_gate)
                    {
                        _inFlight.Remove(key);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>Picks the highest effective-priority waiting job (with aging) and marks it in-flight.</summary>
    private bool TryTakeBest(out TrailerKey key)
    {
        lock (_gate)
        {
            key = default;
            if (_pending.Count == 0)
            {
                return false;
            }

            var nowTicks = DateTime.UtcNow.Ticks;
            var bestEffective = int.MaxValue;
            var bestTicks = long.MaxValue;
            var found = false;
            foreach (var (k, job) in _pending)
            {
                var effective = EffectivePriority(job, nowTicks);
                if (effective < bestEffective || (effective == bestEffective && job.EnqueuedTicks < bestTicks))
                {
                    bestEffective = effective;
                    bestTicks = job.EnqueuedTicks;
                    key = k;
                    found = true;
                }
            }

            if (!found)
            {
                return false;
            }

            _pending.Remove(key);
            _inFlight.Add(key);
            return true;
        }
    }

    /// <summary>Effective priority: base tier improved by one per <see cref="AgingStepSeconds"/> waited (min 1).</summary>
    private static int EffectivePriority(PendingJob job, long nowTicks)
    {
        var waitedSeconds = Math.Max(0, (nowTicks - job.EnqueuedTicks) / TimeSpan.TicksPerSecond);
        var bump = (int)(waitedSeconds / AgingStepSeconds);
        return Math.Max(1, (int)job.Priority - bump);
    }

    /// <summary>Drops the single lowest-priority waiter to make room, if it's less urgent than <paramref name="incoming"/>. Caller holds the lock.</summary>
    private bool TryEvictLowest(TrailerPriority incoming)
    {
        var worstKey = default(TrailerKey);
        var worstPriority = incoming;
        var found = false;
        foreach (var (k, job) in _pending)
        {
            if (job.Priority > worstPriority)
            {
                worstPriority = job.Priority;
                worstKey = k;
                found = true;
            }
        }

        if (found)
        {
            _pending.Remove(worstKey);
            // Consume the evicted item's outstanding permit so the count stays in step with _pending.
            _signal.Wait(0);
        }

        return found;
    }

    private readonly record struct TrailerKey(int TmdbId, MediaType MediaType);

    private readonly record struct PendingJob(TrailerPriority Priority, long EnqueuedTicks);
}
