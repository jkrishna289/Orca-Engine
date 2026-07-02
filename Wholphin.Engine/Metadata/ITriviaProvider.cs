using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Produces short "Did You Know?" trivia facts for a title — Groq-first (when configured), with a
/// thin TMDB-keyword fallback. Results are cached for a long time since trivia rarely changes.
/// </summary>
public interface ITriviaProvider
{
    /// <summary>Returns trivia facts for an item (empty when none can be produced).</summary>
    /// <param name="item">The catalog item.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The trivia facts.</returns>
    Task<IReadOnlyList<string>> GetTriviaAsync(CatalogItem item, CancellationToken ct = default);
}
