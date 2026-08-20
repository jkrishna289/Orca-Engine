using System;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// What a metadata provider can supply. A provider declares its capabilities once; the aggregator
/// intersects them with the fields a row is actually missing, so adding a provider does NOT mean
/// every request calls every provider.
/// </summary>
/// <remarks>
/// Flags rather than one interface per field: TMDB would otherwise register four times as the same
/// singleton and the aggregator would run four loops to ask it four questions.
/// </remarks>
[Flags]
public enum MetadataCapability
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Overview, genres, keywords, people, year, runtime, language, collection.</summary>
    Core = 1,

    /// <summary>Poster and backdrop artwork.</summary>
    Artwork = 2,

    /// <summary>Clear/transparent title logo.</summary>
    Logo = 4,

    /// <summary>IMDb / Rotten Tomatoes / Metacritic / community scores.</summary>
    Ratings = 8,

    /// <summary>External ids (IMDb, TVDB) — the join keys other providers need.</summary>
    Ids = 16,

    /// <summary>A trailer URL.</summary>
    Trailer = 32,

    /// <summary>Per-episode series data.</summary>
    Episodes = 64,
}
