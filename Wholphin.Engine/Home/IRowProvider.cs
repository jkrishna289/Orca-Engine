using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Home;

/// <summary>
/// A pluggable home-row contributor. Providers return domain rows (catalog items + metadata);
/// <see cref="HomeService"/> renders them through the shared card/dedup/layout machinery. New
/// rows are added by registering another provider — HomeService itself never changes.
/// </summary>
public interface IRowProvider
{
    /// <summary>Builds this provider's rows for one home build (possibly none).</summary>
    /// <param name="context">The build context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rows to render, in the provider's preferred relative order.</returns>
    Task<IReadOnlyList<ProviderRow>> BuildAsync(RowContext context, CancellationToken ct = default);
}

/// <summary>The context a row provider builds against.</summary>
public class RowContext
{
    /// <summary>Gets or sets the user the home is for (null = anonymous/global home).</summary>
    public Guid? UserId { get; set; }

    /// <summary>Gets or sets the effective settings for the scope.</summary>
    public EngineSettings Settings { get; set; } = new();

    /// <summary>Gets or sets the user's affinity confidence (0 when anonymous).</summary>
    public double Confidence { get; set; }

    /// <summary>Gets or sets the requested items-per-row.</summary>
    public int RowSize { get; set; }
}

/// <summary>One row a provider wants rendered.</summary>
public class ProviderRow
{
    /// <summary>Gets or sets the stable row id (drives layout priority + client behavior).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the row purpose (feeds the card selector's type decision).</summary>
    public string Purpose { get; set; } = "discover";

    /// <summary>Gets or sets the row style.</summary>
    public RowStyle RowStyle { get; set; } = RowStyle.Standard;

    /// <summary>Gets or sets the items, already in display order.</summary>
    public List<CatalogItem> Items { get; set; } = new();

    /// <summary>Gets or sets per-item justification blurbs (catalog id → text), shown as card subtitles.</summary>
    public Dictionary<long, string>? Reasons { get; set; }

    /// <summary>Gets or sets the layout priority when the profile is warm (lower = higher on screen).</summary>
    public int WarmPriority { get; set; } = 100;

    /// <summary>Gets or sets the layout priority when the profile is cold.</summary>
    public int ColdPriority { get; set; } = 100;
}
