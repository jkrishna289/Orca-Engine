using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Fills in the external ids a catalog row is missing, so providers keyed on something other than
/// TMDB can be asked about it at all.
/// </summary>
/// <remarks>
/// This is the load-bearing step of the whole multi-provider system: OMDb is addressed by IMDb id and
/// Fanart addresses series by TVDB id, so without resolution those providers are simply unreachable
/// no matter how they are configured.
/// </remarks>
public interface IMediaIdentityResolver
{
    /// <summary>
    /// Resolves a row's identity, filling <see cref="CatalogItem.ImdbId"/> and
    /// <see cref="CatalogItem.TvdbId"/> from TMDB when they are missing.
    /// </summary>
    /// <param name="item">The catalog row; mutated in place when new ids are learned. The CALLER saves.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity, as complete as could be resolved.</returns>
    Task<MediaIdentity> ResolveAsync(CatalogItem item, CancellationToken cancellationToken);
}
