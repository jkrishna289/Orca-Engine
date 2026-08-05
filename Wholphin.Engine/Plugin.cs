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
    /// Returns the plugin's web pages: the settings form, and a sidebar entry that leads to the
    /// Orca Observatory.
    /// </summary>
    /// <returns>The pages, settings first.</returns>
    /// <remarks>
    /// <para>
    /// The Observatory itself is NOT a Jellyfin page — it is served standalone from
    /// <c>/orca</c>. Living inside the dashboard meant inheriting its view loader
    /// (jQuery-evaluated inline scripts, a shared DOM with no style isolation, and a teardown event
    /// as the only unmount signal), which cost more than the convenience was worth.
    /// </para>
    /// <para>
    /// The second page here is a redirect stub, not the dashboard returning: a link an admin can
    /// find where they already look. It renders its link as plain markup and only enhances with a
    /// redirect, so it still works if its script never runs.
    /// </para>
    /// <para>
    /// Settings stays first — the installed-plugins card opens whichever page is registered first
    /// for a plugin id, so reordering these would repoint the gear icon.
    /// </para>
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
                "{0}.Configuration.observatory-link.html",
                GetType().Namespace)
        };
    }
}
