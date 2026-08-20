using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// One artwork option, with the signals needed to rank it against another provider's.
/// </summary>
/// <param name="Url">The absolute image URL.</param>
/// <param name="Width">Pixel width, or 0 when the provider does not report it.</param>
/// <param name="Height">Pixel height, or 0 when the provider does not report it.</param>
/// <param name="Language">ISO 639-1 language of any burned-in text, "00" for textless, or null when unknown.</param>
/// <param name="Votes">Community likes/votes, or 0 when the provider does not report them.</param>
/// <remarks>
/// Carrying the dimensions on the candidate is what lets a 1000px Fanart poster beat TMDB's w500
/// WITHOUT storing per-field provenance dimensions in the database.
/// </remarks>
public sealed record ImageCandidate(string Url, int Width, int Height, string? Language, int Votes);

/// <summary>
/// What ONE provider knows about a title. Every member is optional — the whole point is that a
/// provider answers only for the fields it actually has, and the aggregator merges the fragments.
/// </summary>
public sealed class MetadataFragment
{
    /// <summary>Gets the provider name that produced this fragment (its priority token).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Gets the IMDb id.</summary>
    public string? ImdbId { get; init; }

    /// <summary>Gets the TVDB id.</summary>
    public int? TvdbId { get; init; }

    /// <summary>Gets the overview/synopsis.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the production year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the runtime in minutes.</summary>
    public int? RuntimeMinutes { get; init; }

    /// <summary>Gets the ISO 639-1 original language.</summary>
    public string? OriginalLanguage { get; init; }

    /// <summary>Gets the collection/franchise name.</summary>
    public string? CollectionName { get; init; }

    /// <summary>Gets the genre names.</summary>
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

    /// <summary>Gets the keyword/theme names.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>Gets the role-prefixed cast/crew names.</summary>
    public IReadOnlyList<string> People { get; init; } = Array.Empty<string>();

    /// <summary>Gets the poster candidate.</summary>
    public ImageCandidate? Poster { get; init; }

    /// <summary>Gets the backdrop candidate.</summary>
    public ImageCandidate? Backdrop { get; init; }

    /// <summary>Gets the clear-logo candidate.</summary>
    public ImageCandidate? Logo { get; init; }

    /// <summary>Gets the 0-10 community rating.</summary>
    public float? CommunityRating { get; init; }

    /// <summary>Gets external critic scores keyed by source ("imdb", "rt", "metacritic"), each normalized to 0-100.</summary>
    public IReadOnlyDictionary<string, double> Ratings { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Gets a trailer watch URL.</summary>
    public string? TrailerUrl { get; init; }
}
