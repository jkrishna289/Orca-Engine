using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Default <see cref="ITrailerStateStore"/>. Upserts the single <see cref="TrailerAsset"/> row per
/// (tmdbId, mediaType) and emits a <c>trailer.state.*</c> metric on every transition. Fail-soft: a DB
/// error is logged and swallowed so trailer playback/warming continues even if state tracking is down.
/// </summary>
public class TrailerStateStore : ITrailerStateStore
{
    /// <summary>How long to suppress repeat access records for the same title (streaming makes many range requests).</summary>
    private static readonly TimeSpan AccessThrottle = TimeSpan.FromMinutes(1);

    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<TrailerStateStore> _logger;

    // Last time we recorded an access per (tmdbId, mediaType), to throttle DB writes.
    private readonly ConcurrentDictionary<(int, MediaType), DateTime> _lastAccess = new();

    /// <summary>Initializes a new instance of the <see cref="TrailerStateStore"/> class.</summary>
    public TrailerStateStore(IWholphinDbContextFactory factory, IEngineMetrics metrics, ILogger<TrailerStateStore> logger)
    {
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TrailerState> GetStateAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default)
    {
        try
        {
            await using var db = _factory.Create();
            var row = await db.TrailerAssets.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TmdbId == tmdbId && t.MediaType == mediaType, ct)
                .ConfigureAwait(false);
            return row?.State ?? TrailerState.Unknown;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orca Engine: trailer state read failed for tmdb {TmdbId}.", tmdbId);
            return TrailerState.Unknown;
        }
    }

    /// <inheritdoc />
    public async Task SetStateAsync(int tmdbId, MediaType mediaType, TrailerState state, CancellationToken ct = default, string? failureReason = null)
    {
        try
        {
            await using var db = _factory.Create();
            var now = DateTime.UtcNow;
            var row = await db.TrailerAssets
                .FirstOrDefaultAsync(t => t.TmdbId == tmdbId && t.MediaType == mediaType, ct)
                .ConfigureAwait(false);
            if (row is null)
            {
                row = new TrailerAsset { TmdbId = tmdbId, MediaType = mediaType, CreatedAt = now };
                db.TrailerAssets.Add(row);
            }

            row.State = state;
            row.UpdatedAt = now;
            if (state is TrailerState.FailedTemporary or TrailerState.FailedPermanent)
            {
                row.FailureCount++;
                row.FailureReason = failureReason;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _metrics.Increment($"trailer.state.{state}".ToLowerInvariant());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orca Engine: trailer state write ({State}) failed for tmdb {TmdbId}.", state, tmdbId);
        }
    }

    /// <inheritdoc />
    public async Task MarkReadyAsync(int tmdbId, MediaType mediaType, string filePath, long fileBytes, CancellationToken ct = default, int? durationMs = null, int? previewStartMs = null)
    {
        try
        {
            await using var db = _factory.Create();
            var now = DateTime.UtcNow;
            var row = await db.TrailerAssets
                .FirstOrDefaultAsync(t => t.TmdbId == tmdbId && t.MediaType == mediaType, ct)
                .ConfigureAwait(false);
            if (row is null)
            {
                row = new TrailerAsset { TmdbId = tmdbId, MediaType = mediaType, CreatedAt = now };
                db.TrailerAssets.Add(row);
            }

            row.State = TrailerState.Ready;
            row.FilePath = filePath;
            row.FileBytes = fileBytes;
            row.FailureReason = null;
            row.ReadyAt = now;
            row.UpdatedAt = now;
            // Only overwrite metadata when freshly probed, so the reconcile path can't null it out.
            if (durationMs.HasValue)
            {
                row.DurationMs = durationMs;
            }

            if (previewStartMs.HasValue)
            {
                row.PreviewStartMs = previewStartMs;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _metrics.Increment("trailer.state.ready");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orca Engine: trailer mark-ready failed for tmdb {TmdbId}.", tmdbId);
        }
    }

    /// <inheritdoc />
    public async Task RecordAccessAsync(int tmdbId, MediaType mediaType)
    {
        var now = DateTime.UtcNow;
        var key = (tmdbId, mediaType);
        if (_lastAccess.TryGetValue(key, out var last) && now - last < AccessThrottle)
        {
            return;
        }

        _lastAccess[key] = now;
        try
        {
            await using var db = _factory.Create();
            var row = await db.TrailerAssets
                .FirstOrDefaultAsync(t => t.TmdbId == tmdbId && t.MediaType == mediaType)
                .ConfigureAwait(false);
            if (row is null)
            {
                return;
            }

            row.AccessCount = (row.AccessCount ?? 0) + 1;
            row.LastAccessedAt = now;
            await db.SaveChangesAsync().ConfigureAwait(false);
            _metrics.Increment("trailer.cache.hit");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orca Engine: trailer access record failed for tmdb {TmdbId}.", tmdbId);
        }
    }
}
