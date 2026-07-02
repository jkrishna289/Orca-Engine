using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Exposes the resolved engine settings (admin + per-user) and the canonical roaming
/// per-user settings store, so client preferences follow the user across devices.
/// </summary>
[ApiController]
[Route("OrcaEngine/Settings")]
[Produces("application/json")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settings;
    private readonly IUserSettingsStore _userSettings;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsController"/> class.
    /// </summary>
    /// <param name="settings">The layered settings resolver.</param>
    /// <param name="userSettings">The roaming per-user settings store.</param>
    public SettingsController(ISettingsService settings, IUserSettingsStore userSettings)
    {
        _settings = settings;
        _userSettings = userSettings;
    }

    /// <summary>Returns the effective settings (flags + tunables) for a scope.</summary>
    /// <param name="userId">Optional Jellyfin user id; layers that user's roaming overrides.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved settings.</returns>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<EngineSettings>> GetSettings(
        [FromQuery] Guid? userId,
        CancellationToken cancellationToken)
        => await _settings.ResolveAsync(userId, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns just the resolved feature flags for a scope.</summary>
    /// <param name="userId">Optional Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved feature flags.</returns>
    [HttpGet("Features")]
    [AllowAnonymous]
    public async Task<ActionResult<FeatureFlags>> GetFeatures(
        [FromQuery] Guid? userId,
        CancellationToken cancellationToken)
        => (await _settings.ResolveAsync(userId, cancellationToken).ConfigureAwait(false)).Features;

    /// <summary>Returns a user's raw roaming settings (key → JSON value).</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The roaming settings map.</returns>
    [HttpGet("User")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyDictionary<string, string>>> GetUserSettings(
        [FromQuery] Guid userId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest("userId is required.");
        }

        return Ok(await _userSettings.GetAllAsync(userId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Inserts or updates a single roaming setting.</summary>
    /// <param name="request">The setting to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("User")]
    [AllowAnonymous]
    public async Task<ActionResult> SetUserSetting(
        [FromBody] UserSettingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.UserId == Guid.Empty || string.IsNullOrWhiteSpace(request.Key))
        {
            return BadRequest("userId and key are required.");
        }

        await _userSettings.SetAsync(request.UserId, request.Key, request.Value ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Removes a single roaming setting.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="key">The setting key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("User")]
    [AllowAnonymous]
    public async Task<ActionResult> DeleteUserSetting(
        [FromQuery] Guid userId,
        [FromQuery] string key,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(key))
        {
            return BadRequest("userId and key are required.");
        }

        await _userSettings.RemoveAsync(userId, key, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Body for <see cref="SetUserSetting"/>.</summary>
    public class UserSettingRequest
    {
        /// <summary>Gets or sets the Jellyfin user id.</summary>
        public Guid UserId { get; set; }

        /// <summary>Gets or sets the setting key.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Gets or sets the JSON value to store.</summary>
        public string? Value { get; set; }
    }
}
