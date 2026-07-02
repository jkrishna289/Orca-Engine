using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Recommendation;

/// <summary>A themed home row: a mood id, its display title, and the matching items.</summary>
public record MoodRow(string Id, string Title, IReadOnlyList<CatalogItem> Items);

/// <summary>
/// Builds mood-based collection rows (Mind Bending, Dark Thrillers, Feel Good, …) from the catalog,
/// rotating which moods appear day-to-day for variety.
/// </summary>
public interface IMoodCollectionService
{
    /// <summary>Builds the day's mood rows (each with up to <paramref name="rowSize"/> items).</summary>
    /// <param name="rowSize">Maximum items per row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mood rows (empty when the catalog is too sparse).</returns>
    Task<IReadOnlyList<MoodRow>> BuildAsync(int rowSize, CancellationToken ct = default);
}
