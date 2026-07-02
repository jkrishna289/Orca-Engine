using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Home;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Autonomous background worker that keeps personalization fresh without manual intervention:
/// it consumes "profile dirty" signals (raised whenever a behavior event lands), coalesces bursts
/// per user, recomputes that user's affinity vectors off the live request path, then invalidates
/// and precomputes their home read-model cache so the next navigation is instant.
/// </summary>
public class ProfileRecomputeWorker : IProfileRecomputeQueue, IHostedService
{
    /// <summary>How long to let a burst of events for a user accumulate before recomputing.</summary>
    private const int DebounceMs = 1500;

    private readonly IPersonalizationService _personalization;
    private readonly HomeService _home;
    private readonly ICache _cache;
    private readonly ILogger<ProfileRecomputeWorker> _logger;

    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileRecomputeWorker"/> class.
    /// </summary>
    /// <param name="personalization">The personalization service.</param>
    /// <param name="home">The home generator (for cache precompute).</param>
    /// <param name="cache">The L1 cache.</param>
    /// <param name="logger">The logger.</param>
    public ProfileRecomputeWorker(
        IPersonalizationService personalization,
        HomeService home,
        ICache cache,
        ILogger<ProfileRecomputeWorker> logger)
    {
        _personalization = personalization;
        _home = home;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Enqueue(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return;
        }

        _channel.Writer.TryWrite(userId);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => ConsumeAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Orca Engine: profile-recompute worker started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var first in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Coalesce a burst: gather every user signalled within the debounce window so a
                // rapid run of events (e.g. progress ticks) triggers a single recompute each.
                var dirty = new HashSet<Guid> { first };
                try
                {
                    await Task.Delay(DebounceMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                while (_channel.Reader.TryRead(out var more))
                {
                    dirty.Add(more);
                }

                foreach (var userId in dirty)
                {
                    await ProcessAsync(userId, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ProcessAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await _personalization.RecomputeAsync(userId, ct).ConfigureAwait(false);

            // Invalidate the stale home read-model, then precompute the default bundle so the
            // user's next /Home or /Bootstrap is served straight from cache.
            var capabilities = HomeService.DefaultCapabilities();
            _cache.Remove(HomeService.CacheKey(userId, HomeService.DefaultRowSize, capabilities));
            await _home.BuildAsync(capabilities, HomeService.DefaultRowSize, userId, ct).ConfigureAwait(false);

            _logger.LogDebug("Orca Engine: recomputed profile + precomputed home for {UserId}.", userId);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: profile recompute failed for {UserId}.", userId);
        }
    }
}
