using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Keeps the on-disk trailer cache bounded and healthy (Phase 8 of the trailer redesign). The old
/// cache only ever grew — files accumulated forever with no eviction, TTL, or integrity checks. This
/// periodic worker:
///  - <b>verifies</b> Ready assets still have their file (marks vanished ones Expired),
///  - <b>evicts</b> by an LFU×recency score when the total size/count exceeds the caps, never touching
///    pinned (frequently-viewed) trailers, and
///  - <b>expires</b> stale, unwatched, unpinned trailers past the TTL.
/// A complete no-op while the cache is small and healthy.
/// </summary>
public class TrailerCacheManager : IHostedService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    /// <summary>Total cache size cap in bytes (~6 GB). Evict down to this when exceeded.</summary>
    private const long MaxBytes = 6L * 1024 * 1024 * 1024;

    /// <summary>Maximum number of cached trailers. Evict down to this when exceeded.</summary>
    private const int MaxCount = 400;

    /// <summary>Unwatched, unpinned trailers older than this are expired regardless of the size caps.</summary>
    private const int TtlDays = 30;

    /// <summary>At/above this access count a trailer is treated as "frequently viewed" and pinned (never evicted).</summary>
    private const int PinAccessThreshold = 3;

    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<TrailerCacheManager> _logger;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Initializes a new instance of the <see cref="TrailerCacheManager"/> class.</summary>
    public TrailerCacheManager(IWholphinDbContextFactory factory, IEngineMetrics metrics, ILogger<TrailerCacheManager> logger)
    {
        _factory = factory;
        _metrics = metrics;
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
                    await SweepAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Orca Engine: trailer cache sweep failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await using var db = _factory.Create();
        var now = DateTime.UtcNow;

        var ready = await db.TrailerAssets
            .Where(t => t.State == TrailerState.Ready)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // 1. Integrity: a Ready asset whose file has vanished is Expired so it gets re-produced on demand.
        var live = new System.Collections.Generic.List<TrailerAsset>(ready.Count);
        foreach (var asset in ready)
        {
            if (string.IsNullOrEmpty(asset.FilePath) || !File.Exists(asset.FilePath))
            {
                Expire(asset, now, deleteFile: false);
                _metrics.Increment("trailer.cache.verify_missing");
            }
            else
            {
                live.Add(asset);
            }
        }

        // 2. TTL: stale, unwatched, unpinned trailers expire regardless of the size caps.
        var ttlCutoff = now.AddDays(-TtlDays);
        foreach (var asset in live.ToList())
        {
            var lastTouched = asset.LastAccessedAt ?? asset.ReadyAt ?? asset.UpdatedAt;
            if (!IsPinned(asset) && lastTouched < ttlCutoff)
            {
                Expire(asset, now, deleteFile: true);
                live.Remove(asset);
                _metrics.Increment("trailer.cache.expired");
            }
        }

        // 3. Size/count caps: evict the lowest LFU×recency scores among non-pinned assets.
        var totalBytes = live.Sum(a => a.FileBytes ?? 0);
        if (live.Count > MaxCount || totalBytes > MaxBytes)
        {
            var evictable = live
                .Where(a => !IsPinned(a))
                .OrderBy(a => Score(a, now))
                .ToList();

            foreach (var asset in evictable)
            {
                if (live.Count <= MaxCount && totalBytes <= MaxBytes)
                {
                    break;
                }

                totalBytes -= asset.FileBytes ?? 0;
                Expire(asset, now, deleteFile: true);
                live.Remove(asset);
                _metrics.Increment("trailer.cache.evicted");
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("Orca Engine: trailer cache sweep kept {Count} trailers (~{Mb} MB).", live.Count, totalBytes / (1024 * 1024));
    }

    /// <summary>Frequently-viewed or explicitly-pinned trailers are never evicted.</summary>
    private static bool IsPinned(TrailerAsset a) => a.Pinned == true || (a.AccessCount ?? 0) >= PinAccessThreshold;

    /// <summary>LFU×recency: (accesses+1) weighted down as the last view recedes. Lower = evict first.</summary>
    private static double Score(TrailerAsset a, DateTime now)
    {
        var lastTouched = a.LastAccessedAt ?? a.ReadyAt ?? a.UpdatedAt;
        var days = Math.Max(0, (now - lastTouched).TotalDays);
        var recency = 1.0 / (1.0 + days);
        return ((a.AccessCount ?? 0) + 1) * recency;
    }

    private static void Expire(TrailerAsset asset, DateTime now, bool deleteFile)
    {
        if (deleteFile && !string.IsNullOrEmpty(asset.FilePath))
        {
            try
            {
                if (File.Exists(asset.FilePath))
                {
                    File.Delete(asset.FilePath);
                }
            }
            catch
            {
                // best effort — the row is still marked Expired so it re-produces on demand
            }
        }

        asset.State = TrailerState.Expired;
        asset.FilePath = null;
        asset.FileBytes = null;
        asset.UpdatedAt = now;
    }
}
