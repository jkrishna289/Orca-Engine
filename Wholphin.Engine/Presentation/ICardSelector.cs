using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Presentation;

/// <summary>
/// Context for a single card-selection decision.
/// </summary>
public class CardSelectionContext
{
    /// <summary>Gets or sets the row's purpose (e.g., "continue", "people", "recent").</summary>
    public string? RowPurpose { get; set; }

    /// <summary>Gets or sets the row layout style.</summary>
    public RowStyle RowStyle { get; set; } = RowStyle.Standard;

    /// <summary>Gets or sets the item's rank within the row (0-based).</summary>
    public int RankInRow { get; set; }

    /// <summary>Gets or sets a value indicating whether the user has a resume position.</summary>
    public bool HasResume { get; set; }

    /// <summary>Gets or sets the client's render capabilities.</summary>
    public ClientCapabilities Capabilities { get; set; } = new();
}

/// <summary>
/// Decides how a catalog item is rendered (the "dynamic" card decision).
/// </summary>
public interface ICardSelector
{
    /// <summary>Selects a card descriptor for an item in a given context.</summary>
    CardDescriptor Select(CatalogItem item, CardSelectionContext context);
}
