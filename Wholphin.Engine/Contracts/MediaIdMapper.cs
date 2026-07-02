using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Contracts;

/// <summary>
/// Projects a persisted <see cref="CatalogItem"/> into the API's <see cref="MediaIdDto"/>.
/// </summary>
public static class MediaIdMapper
{
    /// <summary>Builds a <see cref="MediaIdDto"/> from a catalog item.</summary>
    public static MediaIdDto ToMediaId(CatalogItem item) => new()
    {
        Source = item.JellyfinItemId.HasValue ? MediaSource.Jellyfin : MediaSource.Tmdb,
        JellyfinId = item.JellyfinItemId,
        TmdbId = item.TmdbId,
        MediaType = item.MediaType,
        Availability = item.Availability,
    };
}
