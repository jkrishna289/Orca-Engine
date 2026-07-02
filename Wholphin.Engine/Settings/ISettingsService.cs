using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Settings;

/// <summary>
/// Resolves the effective <see cref="EngineSettings"/> for a scope by collapsing the layered
/// settings chain: system defaults → admin (plugin configuration) → group → user override.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Resolves the effective settings for a scope. Pass <c>null</c> or <see cref="Guid.Empty"/>
    /// for the global (anonymous) scope; pass a user id to layer that user's roaming overrides.
    /// </summary>
    /// <param name="userId">The Jellyfin user id, or null for the global scope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully-resolved settings.</returns>
    Task<EngineSettings> ResolveAsync(Guid? userId, CancellationToken ct = default);
}
