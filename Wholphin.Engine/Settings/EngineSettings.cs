namespace Wholphin.Engine.Settings;

/// <summary>
/// The effective, fully-resolved engine settings for a scope (the global/anonymous scope or a
/// specific user), produced by collapsing the layered settings chain. This is the read-model the
/// home generator and the bootstrap endpoint consume.
/// </summary>
public class EngineSettings
{
    /// <summary>Gets or sets a value indicating whether the engine is enabled (global kill-switch; clients fall back to their own home when false).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the resolved feature flags.</summary>
    public FeatureFlags Features { get; set; } = new();

    /// <summary>Gets or sets the default items-per-row used when the client does not specify a row size.</summary>
    public int DefaultRowSize { get; set; } = 20;

    /// <summary>Gets or sets the number of items shown in the rotating spotlight billboard.</summary>
    public int SpotlightCount { get; set; } = 5;
}
