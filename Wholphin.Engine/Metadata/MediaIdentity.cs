using System;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// The cross-provider identity of one title. Providers key on different ids — OMDb needs an IMDb id,
/// Fanart needs a TVDB id for series and a TMDB id for movies — so resolving the ids ONCE and passing
/// this around is what lets a second provider be asked about a title at all.
/// </summary>
/// <param name="TmdbId">The TMDB id, when known.</param>
/// <param name="ImdbId">The IMDb id ("tt0816692"), when known.</param>
/// <param name="TvdbId">The TVDB id, when known.</param>
/// <param name="JellyfinItemId">The Jellyfin item id, for library rows.</param>
/// <param name="MediaType">Movie or Series.</param>
/// <param name="Title">The display title.</param>
/// <param name="OriginalTitle">The original-language title, when it differs.</param>
/// <param name="Year">The production year, when known.</param>
/// <remarks>
/// Deliberately a value type: it is passed to every provider on every call, compares structurally,
/// and allocates nothing.
/// </remarks>
public readonly record struct MediaIdentity(
    int? TmdbId,
    string? ImdbId,
    int? TvdbId,
    Guid? JellyfinItemId,
    MediaType MediaType,
    string Title,
    string? OriginalTitle,
    int? Year)
{
    /// <summary>Gets a value indicating whether any external id is known (title-only matching is a last resort).</summary>
    public bool HasAnyId => TmdbId is > 0 || !string.IsNullOrWhiteSpace(ImdbId) || TvdbId is > 0;

    /// <summary>
    /// Gets a stable cache-key segment for this title.
    /// </summary>
    /// <remarks>
    /// Ids only, never the title: cache keys become metric namespaces elsewhere in the engine, and
    /// free text would make the key space unbounded. Two rows for the same title with different ids
    /// are genuinely different lookups, so keying on ids is also the correct identity.
    /// </remarks>
    public string CacheKey => $"{(int)MediaType}:{TmdbId}:{ImdbId}:{TvdbId}";

    /// <summary>Projects a catalog row into its identity.</summary>
    /// <param name="item">The catalog row.</param>
    /// <returns>The identity.</returns>
    public static MediaIdentity FromCatalogItem(CatalogItem item) => new(
        item.TmdbId,
        string.IsNullOrWhiteSpace(item.ImdbId) ? null : item.ImdbId,
        item.TvdbId,
        item.JellyfinItemId,
        item.MediaType,
        item.Title,
        item.OriginalTitle,
        item.ProductionYear);
}
