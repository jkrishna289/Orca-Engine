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
    /// Returns the plugin's web pages: the configuration form, and the Orca Observatory dashboard.
    /// </summary>
    /// <returns>The pages, configuration first.</returns>
    /// <remarks>
    /// Order matters. The installed-plugins card opens the FIRST page registered for a plugin id,
    /// so the settings form has to stay at the head of this list or the gear icon would open the
    /// dashboard instead. <c>DisplayName</c> is what the sidebar actually renders — without it the
    /// menu entry is blank — and only one page may set <c>EnableInMainMenu</c>, because the drawer
    /// keys its list on the plugin id. Page names are global across every installed plugin, hence
    /// the "Orca" prefix.
    /// </remarks>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            DisplayName = "Orca Engine",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.config.html",
                GetType().Namespace)
        };

        yield return new PluginPageInfo
        {
            Name = "OrcaObservatory",
            DisplayName = "Orca Observatory",
            EnableInMainMenu = true,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.observatory.html",
                GetType().Namespace)
        };
    }
}
