using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Behavior;

/// <summary>
/// EF Core / SQLite implementation of <see cref="IBehaviorService"/>. Buffers captured events
/// through an unbounded single-reader channel and persists them one consumer at a time.
/// </summary>
public class BehaviorService : IBehaviorService
{
    private readonly IWholphinDbContextFactory _factory;
    private readonly IProfileRecomputeQueue _recomputeQueue;
    private readonly ILogger<BehaviorService> _logger;

    private readonly Channel<BehaviorEvent> _channel =
        Channel.CreateUnbounded<BehaviorEvent>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Initializes a new instance of the <see cref="BehaviorService"/> class.
    /// </summary>
    /// <param name="factory">The database context factory.</param>
    /// <param name="recomputeQueue">Queue that triggers asynchronous affinity recomputation.</param>
    /// <param name="logger">The logger.</param>
    public BehaviorService(
        IWholphinDbContextFactory factory,
        IProfileRecomputeQueue recomputeQueue,
        ILogger<BehaviorService> logger)
    {
        _factory = factory;
        _recomputeQueue = recomputeQueue;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Record(BehaviorEvent ev)
    {
        if (ev.Timestamp == default)
        {
            ev.Timestamp = DateTime.UtcNow;
        }

        if (!_channel.Writer.TryWrite(ev))
        {
            _logger.LogWarning("Orca Engine: behavior event dropped ({EventType}); channel closed.", ev.EventType);
        }
    }

    /// <inheritdoc />
    public async Task RunConsumerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await using var db = _factory.Create();
                    db.BehaviorEvents.Add(ev);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);

                    // Tell the background worker this user's profile is now stale.
                    _recomputeQueue.Enqueue(ev.UserId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Orca Engine: failed to persist behavior event {EventType}.", ev.EventType);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BehaviorEvent>> GetRecentAsync(Guid? userId, int limit, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        IQueryable<BehaviorEvent> q = db.BehaviorEvents.AsNoTracking();
        if (userId is { } uid)
        {
            q = q.Where(e => e.UserId == uid);
        }

        return await q
            .OrderByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(Guid? userId, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        IQueryable<BehaviorEvent> q = db.BehaviorEvents;
        if (userId is { } uid)
        {
            q = q.Where(e => e.UserId == uid);
        }

        return await q.CountAsync(ct).ConfigureAwait(false);
    }
}
