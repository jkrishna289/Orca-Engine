using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// Default <see cref="IMediaIdentityResolver"/>, backed by TMDB's <c>external_ids</c> block — which
/// rides along on the enrichment call the engine already makes, so resolution costs no extra request.
/// </summary>
public class MediaIdentityResolver : IMediaIdentityResolver
{
    private readonly ITmdbClient _tmdb;
    private readonly IEngineMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaIdentityResolver"/> class.
    /// </summary>
    /// <param name="tmdb">The TMDB port.</param>
    /// <param name="metrics">Operational metrics.</param>
    public MediaIdentityResolver(ITmdbClient tmdb, IEngineMetrics metrics)
    {
        _tmdb = tmdb;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public async Task<MediaIdentity> ResolveAsync(CatalogItem item, CancellationToken cancellationToken)
    {
        var identity = MediaIdentity.FromCatalogItem(item);

        // Series need a TVDB id (Fanart keys them by it); movies only ever need IMDb. Asking TMDB for
        // a tvdb_id it will never have for a film would spend a call to learn nothing.
        var needsImdb = string.IsNullOrWhiteSpace(item.ImdbId);
        var needsTvdb = item.TvdbId is not > 0 && item.MediaType == Data.Enums.MediaType.Series;

        if (item.TmdbId is not > 0 || (!needsImdb && !needsTvdb))
        {
            return identity;
        }

        var enrichment = await _tmdb.EnrichAsync(item.TmdbId.Value, item.MediaType, cancellationToken).ConfigureAwait(false);
        if (enrichment is null)
        {
            return identity;
        }

        var learned = false;
        if (needsImdb && !string.IsNullOrWhiteSpace(enrichment.ImdbId))
        {
            item.ImdbId = enrichment.ImdbId;
            learned = true;
        }

        if (needsTvdb && enrichment.TvdbId is > 0)
        {
            item.TvdbId = enrichment.TvdbId;
            learned = true;
        }

        if (learned)
        {
            _metrics.Increment("metadata.ids.resolved");
        }

        return MediaIdentity.FromCatalogItem(item);
    }
}
