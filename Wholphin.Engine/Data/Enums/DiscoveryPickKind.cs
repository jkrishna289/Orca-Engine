namespace Wholphin.Engine.Data.Enums;

/// <summary>
/// Why a discovery pick exists — the category of justification that put an externally-sourced
/// title in front of a user. Every external item shown on the home screen must trace back to a
/// pick row carrying one of these kinds plus a human-readable reason.
/// </summary>
public enum DiscoveryPickKind
{
    /// <summary>Matches the user's taste profile (embedding similarity + affinity overlap).</summary>
    TasteMatch = 0,

    /// <summary>Related to a specific title the user watched (seeded recommendation).</summary>
    BecauseYouWatched = 1,

    /// <summary>Globally trending this week (TMDB trending; not a personal-taste claim).</summary>
    Trending = 2,

    /// <summary>Popular in a specific country (regional watch-provider popularity).</summary>
    Country = 3,

    /// <summary>Controlled exploration — outside the user's usual taste, honestly labeled.</summary>
    Exploration = 4,

    /// <summary>Chosen by the configured LLM from the viewer's taste history (unseeded AI pick).</summary>
    LlmPick = 5
}
