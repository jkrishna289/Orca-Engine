using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Settings;

/// <summary>
/// Canonical roaming per-user settings store (key → JSON value). Because it lives server-side,
/// a user's preferences follow them across every device instead of being scattered across
/// per-device client storage.
/// </summary>
public interface IUserSettingsStore
{
    /// <summary>Returns all roaming settings for a user as a key → JSON-value map.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user's settings (empty when none exist).</returns>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Returns a single roaming setting, or null when unset.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="key">The setting key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored JSON value, or null.</returns>
    Task<string?> GetAsync(Guid userId, string key, CancellationToken ct = default);

    /// <summary>Inserts or updates a roaming setting.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="key">The setting key.</param>
    /// <param name="valueJson">The JSON value to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the value is persisted.</returns>
    Task SetAsync(Guid userId, string key, string valueJson, CancellationToken ct = default);

    /// <summary>Removes a roaming setting (no-op when absent).</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="key">The setting key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the value is removed.</returns>
    Task RemoveAsync(Guid userId, string key, CancellationToken ct = default);
}
