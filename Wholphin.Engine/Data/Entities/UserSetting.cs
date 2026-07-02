using System;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// A roaming per-user setting (canonical store, so prefs follow the user across devices).
/// Stored as a JSON value under a string key.
/// </summary>
public class UserSetting
{
    /// <summary>Gets or sets the Jellyfin user id (composite key).</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the setting key (composite key).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the JSON-serialized value.</summary>
    public string ValueJson { get; set; } = string.Empty;

    /// <summary>Gets or sets when the setting was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
