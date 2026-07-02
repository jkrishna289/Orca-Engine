using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Periodically warms the trailer cache for the most prominent available titles (top-rated WatchNow),
/// so card-focus previews hit the cache. Gated on the <c>TrailerPrebuffer</c> flag and the presence of
/// yt-dlp/ffmpeg — a complete no-op (and cheap) when either is off/absent.
/// </summary>
public class TrailerPrebufferWorker : IHostedService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int PrebufferCount = 12;

    private readonly ITrailerService _trailers;
    private readonly ISettingsService _settings;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ILogger<TrailerPrebufferWorker> _logger;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerPrebufferWorker"/> class.
    /// </summary>
    public TrailerPrebufferWorker(
        ITrailerService trailers,
        ISettingsService settings,
        IWholphinDbContextFactory factory,
        ILogger<TrailerPrebufferWorker> logger)
    {
        _trailers = trailers;
        _settings = settings;
        _factory = factory;
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
        await using var db = _factory.Create();
        var items = await db.CatalogItems
            .Where(c => c.TmdbId != null && c.Availability == AvailabilityState.WatchNow)
            .OrderByDescending(c => c.CommunityRating)
            .Take(PrebufferCount)
            .Select(c => new { TmdbId = c.TmdbId!.Value, c.MediaType })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var cached = await _trailers.PrebufferAsync(items.Select(i => (i.TmdbId, i.MediaType)), ct).ConfigureAwait(false);
        if (cached > 0)
        {
            _logger.LogInformation("Orca Engine: pre-buffered {Count} trailers.", cached);
        }
    }
}
