using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Playback;

/// <summary>One persisted playback lease per user/title. New leases and native playback retire old writers.</summary>
public sealed class PlaybackProgressService : IPlaybackProgressService
{
    // Singleton in DI: also serializes library reconciliation with incoming playback reports.
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly IWholphinDbContextFactory _factory;
    public PlaybackProgressService(IWholphinDbContextFactory factory) => _factory = factory;

    public async Task<PlaybackProgressRecord> StartAsync(Guid userId, PlaybackIdentity identity, string? title = null, int? year = null, CancellationToken ct = default)
    {
        ValidateUser(userId);
        identity = identity.Validate();
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.Create();
            var row = await Find(db, userId, identity).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new PlaybackProgressRecord { UserId = userId, TmdbId = identity.TmdbId, MediaType = identity.MediaType, Season = identity.Season, Episode = identity.Episode };
                db.PlaybackProgress.Add(row);
            }

            row.ActiveSessionId = Guid.NewGuid();
            row.SessionStartedAt = DateTime.UtcNow;
            row.LastSequence = -1;
            row.SessionStopped = false;
            if (!string.IsNullOrWhiteSpace(title)) row.Title = title.Trim()[..Math.Min(title.Trim().Length, 1024)];
            if (year is >= 1800 and <= 9999) row.Year = year;
            // Opening the source picker is not a rewatch: leave progress/completion intact until playback reports.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return row;
        }
        finally { _writes.Release(); }
    }

    public async Task<PlaybackReportResult> ReportAsync(Guid userId, Guid sessionId, PlaybackProgressUpdate update, CancellationToken ct = default)
    {
        // Preserve arrival order against native events waiting on the same write gate.
        var receivedAt = DateTime.UtcNow;
        ValidateUser(userId);
        if (sessionId == Guid.Empty || update.Sequence < 0 || update.PositionMs < 0 || update.DurationMs <= 0 || update.PositionMs > update.DurationMs)
            throw new ArgumentException("Progress requires a session, nonnegative sequence/position and a positive duration containing the position.");
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.Create();
            var row = await db.PlaybackProgress.SingleOrDefaultAsync(p => p.UserId == userId && p.ActiveSessionId == sessionId, ct).ConfigureAwait(false);
            if (row is null) return PlaybackReportResult.NotFound;
            if (update.Sequence <= row.LastSequence) return PlaybackReportResult.Duplicate;
            if (row.SessionStopped) return PlaybackReportResult.Superseded;
            row.PositionMs = update.PositionMs;
            row.DurationMs = update.DurationMs;
            row.Completed = update.Completed;
            row.LastSequence = update.Sequence;
            row.SessionStopped = update.Stopped;
            row.UpdatedAt = receivedAt;
            row.Revision++;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return PlaybackReportResult.Accepted;
        }
        finally { _writes.Release(); }
    }

    public async Task<PlaybackProgressRecord?> GetAsync(Guid userId, PlaybackIdentity identity, CancellationToken ct = default)
    {
        ValidateUser(userId);
        identity = identity.Validate();
        await using var db = _factory.Create();
        return await Find(db, userId, identity).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PlaybackProgressRecord>> GetRecentAsync(Guid userId, int limit = 100, CancellationToken ct = default)
    {
        ValidateUser(userId);
        await using var db = _factory.Create();
        return await db.PlaybackProgress.AsNoTracking().Where(p => p.UserId == userId && p.Revision > 0)
            .OrderByDescending(p => p.UpdatedAt).Take(Math.Max(1, limit)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PlaybackProgressRecord>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        return await db.PlaybackProgress.AsNoTracking().Where(p => p.Revision > 0 && (p.AppliedRevision < p.Revision || p.JellyfinItemId == null))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task ReconcileAsync(long recordId, Func<PlaybackProgressRecord, CancellationToken, Task<bool>> apply, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.Create();
            var row = await db.PlaybackProgress.SingleOrDefaultAsync(p => p.Id == recordId, ct).ConfigureAwait(false);
            if (row is null || row.Revision == 0) return;
            if (await apply(row, ct).ConfigureAwait(false)) row.AppliedRevision = row.Revision;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    public async Task ObserveLocalAsync(Guid userId, PlaybackIdentity identity, Guid jellyfinItemId, long positionMs, long durationMs, bool completed, DateTime observedAt, CancellationToken ct = default)
    {
        ValidateUser(userId);
        identity = identity.Validate();
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.Create();
            var row = await Find(db, userId, identity).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new PlaybackProgressRecord { UserId = userId, TmdbId = identity.TmdbId, MediaType = identity.MediaType, Season = identity.Season, Episode = identity.Episode };
                db.PlaybackProgress.Add(row);
            }
            // Native playback begins a new writer generation. Old stream reports can have a
            // later receipt time while this event waits for the gate; they must still lose.
            if (observedAt < row.LastNativeAt || (observedAt < row.SessionStartedAt && observedAt < row.UpdatedAt)) return;
            ApplyLocalState(row, jellyfinItemId, positionMs, durationMs, completed, observedAt);
            row.AppliedRevision = row.Revision;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    /// <summary>Adopts authoritative native playback under ReconcileAsync's lock, including explicit reset/rewatch.</summary>
    public static void ApplyLocalState(PlaybackProgressRecord row, Guid jellyfinItemId, long positionMs, long durationMs, bool completed, DateTime observedAt)
    {
        row.JellyfinItemId = jellyfinItemId;
        row.DurationMs = Math.Max(0, durationMs);
        row.PositionMs = Math.Clamp(positionMs, 0, row.DurationMs);
        row.Completed = completed;
        row.UpdatedAt = observedAt;
        row.LastNativeAt = observedAt;
        row.Revision++;
        // A native event can wait behind I/O while a new stream lease starts. It must not retire
        // that newer lease merely because its callback finished later.
        row.SessionStopped = row.SessionStopped || observedAt >= row.SessionStartedAt;
    }

    private static IQueryable<PlaybackProgressRecord> Find(WholphinDbContext db, Guid userId, PlaybackIdentity identity) =>
        db.PlaybackProgress.Where(p => p.UserId == userId && p.TmdbId == identity.TmdbId && p.MediaType == identity.MediaType && p.Season == identity.Season && p.Episode == identity.Episode);

    private static void ValidateUser(Guid userId)
    {
        if (userId == Guid.Empty) throw new ArgumentException("An authenticated user is required.");
    }
}
