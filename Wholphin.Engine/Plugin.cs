using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Wholphin.Engine.Configuration;

namespace Wholphin.Engine;

/// <summary>
/// The Orca Engine plugin — the server-side brain/backbone for the Orca X Android TV client.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the singleton plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the UTC time the plugin assembly was loaded (for uptime reporting).</summary>
    public static DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <inheritdoc />
    public override string Name => "Orca Engine";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b9f3c2e1-7a4d-4e6b-9c8f-1d2a3b4c5d6e");

    /// <inheritdoc />
    public override string Description =>
        "Server-side intelligence engine for the Orca X TV app: personalization, "
        + "recommendations, caching, and centralized administration.";

    /// <summary>
    /// Returns the plugin's web configuration page.
    /// </summary>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.config.html",
                GetType().Namespace)
        };
    }
}
