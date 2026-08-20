using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Wholphin.Engine.Diagnostics;

/// <summary>
/// In-process <see cref="IEngineAlerts"/>. A handful of keys at most, so a dictionary is the whole
/// data structure. Cleared on restart along with everything else in <see cref="IEngineEvents"/> —
/// a condition that is still true will re-raise the first time the failing path runs again.
/// </summary>
public sealed class EngineAlerts : IEngineAlerts
{
    private readonly ConcurrentDictionary<string, EngineAlert> _active = new(StringComparer.Ordinal);
    private readonly IEngineEvents _events;

    /// <summary>Initializes a new instance of the <see cref="EngineAlerts"/> class.</summary>
    /// <param name="events">The structured event log (alerts also land in the live log).</param>
    public EngineAlerts(IEngineEvents events) => _events = events;

    /// <inheritdoc />
    public void Raise(string key, string level, string title, string detail = "")
    {
        var now = DateTime.UtcNow;
        var isNew = !_active.ContainsKey(key);

        _active.AddOrUpdate(
            key,
            _ => new EngineAlert(key, level, title, detail, now, now, 1),
            (_, existing) => existing with
            {
                Level = level,
                Title = title,
                Detail = detail,
                LastSeenUtc = now,
                Count = existing.Count + 1,
            });

        // Only the transition is logged. A condition re-raised on every 6-hourly index rebuild
        // would otherwise bury the live log in the same line forever.
        if (isNew)
        {
            var component = key.Split('.', 2)[0];
            _events.Emit(level == "critical" ? "error" : "warn", component, "alert.raised", data: title);
        }
    }

    /// <inheritdoc />
    public void Clear(string key)
    {
        if (_active.TryRemove(key, out var cleared))
        {
            var component = key.Split('.', 2)[0];
            _events.Emit("info", component, "alert.cleared", data: cleared.Title);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EngineAlert> Active() => _active.Values
        .OrderByDescending(a => a.Level == "critical")
        .ThenByDescending(a => a.LastSeenUtc)
        .ToList();
}
