namespace Wholphin.Engine.Trailer;

/// <summary>
/// Relative importance of a queued trailer job. Lower value = higher priority (served first). The
/// single <see cref="ITrailerQueue"/> always favours user-visible content, while queue aging (see
/// <c>TrailerQueueWorker</c>) guarantees even the lowest tiers are eventually served, so background
/// warming can never be starved indefinitely.
/// </summary>
public enum TrailerPriority
{
    /// <summary>The card the user is focused on right now.</summary>
    FocusedCard = 1,

    /// <summary>The current hero/billboard spotlight.</summary>
    HeroBillboard = 2,

    /// <summary>The card the user is predicted to focus next (neighbour / navigation direction).</summary>
    LikelyNextCard = 3,

    /// <summary>Other cards in the currently visible row.</summary>
    VisibleRow = 4,

    /// <summary>A title whose detail page was just opened (high trailer intent).</summary>
    DetailPage = 5,

    /// <summary>Titles in recommendation rows further down the home.</summary>
    RecommendationRow = 6,

    /// <summary>The periodic background pre-buffer worker.</summary>
    BackgroundWorker = 7,

    /// <summary>The scheduled bulk warming task.</summary>
    ScheduledWarming = 8,

    /// <summary>Opportunistic maintenance / re-warming of evicted content.</summary>
    Maintenance = 9,
}
