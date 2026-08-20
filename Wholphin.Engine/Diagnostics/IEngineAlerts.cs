using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Diagnostics;

/// <summary>
/// One condition that is wrong <em>right now</em> and needs an operator to act.
/// </summary>
/// <param name="Key">Stable identity ("embedding.fallback"). Re-raising the same key updates rather than duplicates.</param>
/// <param name="Level">"critical" or "warn". Critical paints the Observatory banner red.</param>
/// <param name="Title">One line, shown in the banner.</param>
/// <param name="Detail">What to actually do about it.</param>
/// <param name="FirstSeenUtc">When the condition first appeared.</param>
/// <param name="LastSeenUtc">The most recent time it was re-raised.</param>
/// <param name="Count">How many times it has been raised since it was last cleared.</param>
public sealed record EngineAlert(
    string Key,
    string Level,
    string Title,
    string Detail,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    long Count);

/// <summary>
/// Sticky, self-clearing health conditions — the "something is broken and it is still broken" state
/// that <see cref="IEngineEvents"/> deliberately cannot hold.
/// </summary>
/// <remarks>
/// <para>
/// Engine events are a bounded ring: a warning emitted at 3am is gone by morning, and an operator
/// who wasn't watching the live log never learns that the cloud embedding provider silently fell
/// back to TF-IDF weeks ago. Alerts stay up until the condition that raised them stops happening,
/// so the dashboard can answer "is the engine degraded" without anyone having caught the moment.
/// </para>
/// <para>Raise on failure, <see cref="Clear"/> on the next success. An alert nobody clears is a bug.</para>
/// </remarks>
public interface IEngineAlerts
{
    /// <summary>Raises (or refreshes) an alert. Idempotent per key.</summary>
    /// <param name="key">Stable key for the condition.</param>
    /// <param name="level">"critical" or "warn".</param>
    /// <param name="title">One-line summary for the banner.</param>
    /// <param name="detail">What the operator should do about it.</param>
    void Raise(string key, string level, string title, string detail = "");

    /// <summary>Clears an alert. Safe to call when nothing is raised.</summary>
    /// <param name="key">The key to clear.</param>
    void Clear(string key);

    /// <summary>Returns the active alerts, critical first then newest.</summary>
    /// <returns>The active alerts.</returns>
    IReadOnlyList<EngineAlert> Active();
}
