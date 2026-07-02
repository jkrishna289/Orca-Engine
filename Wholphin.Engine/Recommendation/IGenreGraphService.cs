using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Serves the catalog-derived genre relationship graph (Crime → Thriller → Mystery, …). Built
/// offline from genre co-occurrence and cached; needs no schema change.
/// </summary>
public interface IGenreGraphService
{
    /// <summary>Returns the genres most related to a source genre, strongest first.</summary>
    /// <param name="genre">The source genre.</param>
    /// <param name="limit">Maximum related genres to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The related genres (empty when the genre is unknown).</returns>
    Task<IReadOnlyList<GenreLink>> GetRelatedAsync(string genre, int limit, CancellationToken ct = default);

    /// <summary>Returns the full genre association graph (source genre → ranked related genres).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The association graph.</returns>
    Task<IReadOnlyDictionary<string, List<GenreLink>>> GetGraphAsync(CancellationToken ct = default);
}
