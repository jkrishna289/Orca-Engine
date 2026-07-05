using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Home;

/// <summary>
/// Powers the "New Since You Were Away" row with strict <em>release/availability events</em> since
/// the user's last visit — a movie that became available, or a series whose episode aired/downloaded
/// while they were gone. It deliberately excludes backlog (old unwatched seasons belong in
/// "Continue the Story", not here). Last-seen is tracked per user in the roaming settings store.
/// </summary>
public interface INewSinceProvider
{
    /// <summary>Returns release events new since the user's last-seen timestamp, newest first.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="limit">Maximum items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The events (empty when none / no user).</returns>
    Task<NewSinceResult> GetAsync(Guid userId, int limit, CancellationToken ct = default);

    /// <summary>Stamps the user's last-seen time to now (call after a real home visit).</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the stamp is persisted.</returns>
    Task MarkSeenAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>The result of a "New Since You Were Away" build: the event items plus their "what changed" blurbs.</summary>
/// <param name="Items">The catalog items to render, newest event first.</param>
/// <param name="Reasons">Per-item event blurbs (catalog id → text) shown as card subtitles.</param>
public sealed record NewSinceResult(
    IReadOnlyList<CatalogItem> Items,
    IReadOnlyDictionary<long, string> Reasons)
{
    /// <summary>An empty result (no events).</summary>
    public static NewSinceResult Empty { get; } =
        new(Array.Empty<CatalogItem>(), new Dictionary<long, string>());
}
