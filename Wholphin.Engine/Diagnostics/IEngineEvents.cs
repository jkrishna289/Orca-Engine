using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Diagnostics;

/// <summary>
/// One thing the engine did, at a point in time. This is what <see cref="IEngineMetrics"/> counters
/// cannot be: counters are monotonic totals with no clock, so they can answer "how many TMDB calls
/// failed" but never "what failed, when, and why".
/// </summary>
/// <param name="Seq">Monotonic sequence number. Doubles as the stream cursor for resuming.</param>
/// <param name="At">When it happened (UTC).</param>
/// <param name="Level">"info", "warn" or "error".</param>
/// <param name="Component">Coarse subsystem — the first segment of the metric key ("home", "tmdb", "trailer").</param>
/// <param name="Event">What happened — the rest of the metric key ("row.foryou", "enrich").</param>
/// <param name="ElapsedMs">How long it took, when the call site timed it.</param>
/// <param name="Data">A short human-readable detail. NEVER a URL — see <see cref="IEngineEvents"/>.</param>
/// <param name="Exception">Full exception text (type, message, stack) for errors.</param>
public sealed record EngineEvent(
    long Seq,
    DateTime At,
    string Level,
    string Component,
    string Event,
    long? ElapsedMs,
    string? Data,
    string? Exception);

/// <summary>
/// A bounded in-process log of structured engine events, backing the Observatory's live log,
/// timeline, and error detail.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a plugin cannot capture Jellyfin's log stream. Jellyfin calls
/// <c>.UseSerilog()</c> with a null provider collection, so a DI-registered
/// <c>ILoggerProvider</c> is never enumerated, and <c>SerilogLoggerFactory.AddProvider</c> is a
/// no-op. Registering a Serilog sink from a plugin doesn't work either — sink assemblies are
/// resolved from the host's dependency context, which doesn't include <c>plugins/</c>. So the
/// engine emits its own structured events instead of trying to tail someone else's.
/// </para>
/// <para>
/// <b>Never pass a URL as <c>data</c>.</b> TMDB request URLs carry the API key in the query string.
/// Record host and path only. These events are exported and downloaded from the dashboard.
/// </para>
/// <para>Contents are lost on restart, deliberately — this is a diagnostic buffer, not an audit log.</para>
/// </remarks>
public interface IEngineEvents
{
    /// <summary>Gets the highest sequence number issued so far (0 when nothing has been emitted).</summary>
    long LastSeq { get; }

    /// <summary>Records one event.</summary>
    /// <param name="level">"info", "warn" or "error". Anything other than "info"/"warn" is stored in the error ring.</param>
    /// <param name="component">Coarse subsystem, e.g. "home".</param>
    /// <param name="event">What happened, e.g. "row.foryou".</param>
    /// <param name="elapsedMs">Duration, when known.</param>
    /// <param name="data">Short detail. Not a URL.</param>
    /// <param name="exception">The failure, for error events.</param>
    void Emit(string level, string component, string @event, long? elapsedMs = null, string? data = null, Exception? exception = null);

    /// <summary>Returns the most recent events, newest first.</summary>
    /// <param name="limit">Maximum events to return.</param>
    /// <param name="errorsOnly">Return only errors (which are retained far longer than routine traffic).</param>
    /// <returns>The events, newest first.</returns>
    IReadOnlyList<EngineEvent> Recent(int limit = 200, bool errorsOnly = false);

    /// <summary>Returns everything emitted after a sequence number, oldest first — the stream cursor.</summary>
    /// <param name="seq">The last sequence number the caller has already seen.</param>
    /// <param name="limit">Maximum events to return.</param>
    /// <returns>The events, oldest first.</returns>
    IReadOnlyList<EngineEvent> Since(long seq, int limit = 500);
}
