namespace Wholphin.Engine.Catalog;

/// <summary>Which "Coming Soon (For You)" sub-type an upcoming title qualifies for (if any).</summary>
public enum ComingSoonKind
{
    /// <summary>Below intent threshold / actively not-interested — not shown.</summary>
    Excluded,

    /// <summary>The next episode of a series the user monitors (explicit tracking).</summary>
    NextEpisode,

    /// <summary>A new season of a series the user has invested in (watched a prior season / follows the franchise).</summary>
    NewSeason,

    /// <summary>A high-quality, taste-aligned upcoming title (global relevance).</summary>
    Trending,
}

/// <summary>
/// Pure inclusion/sub-type rules for the "Coming Soon (For You)" row. Turns the raw *arr calendar
/// into a relevance decision: explicit interest (monitored / invested) always leads; everything else
/// must clear an intent + quality bar and must not be actively disliked. No I/O — unit-testable.
/// </summary>
public static class ComingSoonClassifier
{
    /// <summary>Net taste alignment (<see cref="Personalization.CatalogFeatures.Dot"/>) required for a trending pick.</summary>
    public const double IntentThreshold = 0.0;

    /// <summary>Taste alignment at/below which a title is treated as actively not-interested.</summary>
    public const double NotInterestedThreshold = -0.5;

    /// <summary>Community rating a trending pick needs (when a rating is known).</summary>
    public const double MinTrendingRating = 6.5;

    /// <summary>Confidence below which the profile is cold and the taste gate is relaxed.</summary>
    public const double WarmConfidence = 0.40;

    /// <summary>
    /// Classifies one upcoming title.
    /// </summary>
    /// <param name="isSeries">Whether the title is a series (vs a movie).</param>
    /// <param name="isMonitored">Whether the *arr instance monitors this series/season (or movie).</param>
    /// <param name="userWatchedPrior">Whether the user has watched a prior/current season of this series.</param>
    /// <param name="followsFranchise">Whether the user has positive affinity for the title's franchise.</param>
    /// <param name="intentScore">The item's taste-affinity dot score.</param>
    /// <param name="confidence">The user's affinity confidence (0 when anonymous/cold).</param>
    /// <param name="communityRating">The community rating (0-10), if known.</param>
    /// <returns>The sub-type, or <see cref="ComingSoonKind.Excluded"/>.</returns>
    public static ComingSoonKind Classify(
        bool isSeries,
        bool isMonitored,
        bool userWatchedPrior,
        bool followsFranchise,
        double intentScore,
        double confidence,
        double? communityRating)
    {
        // Explicit interest wins and bypasses the negative/quality gates.
        if (isMonitored)
        {
            return isSeries ? ComingSoonKind.NextEpisode : ComingSoonKind.Trending;
        }

        if (isSeries && (userWatchedPrior || followsFranchise))
        {
            return ComingSoonKind.NewSeason;
        }

        // Actively disliked → never surface as discovery.
        if (intentScore <= NotInterestedThreshold)
        {
            return ComingSoonKind.Excluded;
        }

        // Trending upcoming: good global quality AND (taste-aligned, or a cold profile we can't judge).
        var qualityOk = communityRating is null || communityRating >= MinTrendingRating;
        var tasteOk = confidence < WarmConfidence || intentScore > IntentThreshold;
        return qualityOk && tasteOk ? ComingSoonKind.Trending : ComingSoonKind.Excluded;
    }

    /// <summary>The short row-label prefix for a sub-type.</summary>
    /// <param name="kind">The sub-type.</param>
    /// <returns>The label prefix (empty for <see cref="ComingSoonKind.Excluded"/>).</returns>
    public static string LabelPrefix(ComingSoonKind kind) => kind switch
    {
        ComingSoonKind.NextEpisode => "Next episode",
        ComingSoonKind.NewSeason => "New season",
        ComingSoonKind.Trending => "Coming soon",
        _ => string.Empty,
    };
}
