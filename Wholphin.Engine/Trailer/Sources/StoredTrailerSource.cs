using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Metadata;

namespace Wholphin.Engine.Trailer.Sources;

/// <summary>
/// The trailer URL already saved on the catalog row by an earlier enrichment pass.
/// </summary>
public class StoredTrailerSource : ITrailerSource
{
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>Initializes a new instance of the <see cref="StoredTrailerSource"/> class.</summary>
    /// <param name="factory">The database context factory.</param>
    public StoredTrailerSource(IWholphinDbContextFactory factory) => _factory = factory;

    /// <inheritdoc />
    public string Name => "stored";

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(MediaIdentity identity, string? preferredLanguage, CancellationToken cancellationToken)
    {
        if (identity.TmdbId is not > 0)
        {
            return null;
        }

        await using var db = _factory.Create();
        return await db.CatalogItems
            .AsNoTracking()
            .Where(c => c.TmdbId == identity.TmdbId)
            .Select(c => c.TrailerUrl)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
