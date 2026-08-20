using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Behavior;

/// <summary>
/// How far one user's import has got. <see cref="ItemsScanned"/> over <see cref="ItemsTotal"/> is
/// the per-user progress bar; <see cref="Confidence"/> is filled in once the profile is rebuilt, so
/// the operator can see the import actually moved the number it was supposed to move.
/// </summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="UserName">The Jellyfin user name.</param>
/// <param name="State">"pending", "running", "done" or "failed".</param>
/// <param name="ItemsScanned">Library items examined so far.</param>
/// <param name="ItemsTotal">Library items with any user data to examine.</param>
/// <param name="EventsImported">Behavior events written for this user.</param>
/// <param name="Unresolved">Watched items with no matching catalog row (they teach the engine nothing).</param>
/// <param name="Confidence">Profile confidence after the rebuild (0-1).</param>
/// <param name="Error">Why this user failed, when they did.</param>
public sealed record HistoryImportUser(
    Guid UserId,
    string UserName,
    string State,
    int ItemsScanned,
    int ItemsTotal,
    int EventsImported,
    int Unresolved,
    double Confidence,
    string? Error);

/// <summary>
/// A whole-run snapshot of the watch-history import, safe to poll while it runs.
/// </summary>
/// <param name="Running">Whether an import is in flight.</param>
/// <param name="Phase">Human-readable stage ("scanning Mom", "rebuilding profiles", "idle").</param>
/// <param name="StartedUtc">When the run started.</param>
/// <param name="FinishedUtc">When it finished, if it has.</param>
/// <param name="UsersTotal">How many users are in the run.</param>
/// <param name="UsersDone">How many have finished.</param>
/// <param name="EventsImported">Total events written across all users.</param>
/// <param name="Error">Why the run failed, when it did.</param>
/// <param name="Users">Per-user detail, in run order.</param>
public sealed record HistoryImportProgress(
    bool Running,
    string Phase,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    int UsersTotal,
    int UsersDone,
    int EventsImported,
    string? Error,
    IReadOnlyList<HistoryImportUser> Users);

/// <summary>
/// Backfills the engine's behavior log from Jellyfin's existing per-user watch history.
/// </summary>
/// <remarks>
/// <para>
/// Live capture (<see cref="BehaviorEntryPoint"/>) only ever sees what happens after the plugin is
/// installed, so on a server with years of viewing every profile starts empty and every
/// recommendation is a guess. This reads what Jellyfin already knows — played, play count,
/// favorite, rating — and synthesizes the behavior events that capture would have produced.
/// </para>
/// <para>
/// Idempotent: a re-run deletes this importer's own previous events for each user before writing
/// again, so it can be re-run after a library fix without double-counting. Live-captured events are
/// never touched.
/// </para>
/// </remarks>
public interface IWatchHistoryImporter
{
    /// <summary>Gets the current (or last completed) run's progress.</summary>
    HistoryImportProgress Progress { get; }

    /// <summary>
    /// Starts an import in the background, unless one is already running.
    /// </summary>
    /// <returns><c>true</c> when a run was started; <c>false</c> when one was already in flight.</returns>
    bool TryStart();
}
