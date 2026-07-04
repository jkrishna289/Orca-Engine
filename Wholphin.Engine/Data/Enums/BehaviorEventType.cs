namespace Wholphin.Engine.Data.Enums;

/// <summary>
/// Types of user-behavior signals captured in the append-only event log.
/// Higher-value signals (completed, favorite, request) weigh more in personalization.
/// </summary>
public enum BehaviorEventType
{
    /// <summary>Unknown / unmapped.</summary>
    Unknown = 0,

    /// <summary>Playback started.</summary>
    PlaybackStarted = 1,

    /// <summary>Playback progress reported.</summary>
    PlaybackProgress = 2,

    /// <summary>Playback stopped (not necessarily finished).</summary>
    PlaybackStopped = 3,

    /// <summary>Item watched to completion.</summary>
    PlaybackCompleted = 4,

    /// <summary>Item marked as favorite.</summary>
    MarkedFavorite = 10,

    /// <summary>Item unmarked as favorite.</summary>
    UnmarkedFavorite = 11,

    /// <summary>Item rated.</summary>
    Rated = 12,

    /// <summary>Item marked played.</summary>
    MarkedPlayed = 13,

    /// <summary>Item marked unplayed.</summary>
    MarkedUnplayed = 14,

    /// <summary>Explicit thumbs-up (strongest positive signal).</summary>
    ThumbsUp = 15,

    /// <summary>Explicit thumbs-down (strongest negative signal; excludes item from candidates).</summary>
    ThumbsDown = 16,

    /// <summary>A search was performed.</summary>
    Searched = 20,

    /// <summary>An item was browsed/impressed.</summary>
    Browsed = 21,

    /// <summary>A card was shown on screen (impression). Recorded for funnel/negative analysis; no positive taste weight.</summary>
    CardImpression = 22,

    /// <summary>A card was focused; <see cref="BehaviorEvent.Value"/> carries the focus/dwell duration in seconds.</summary>
    CardFocused = 23,

    /// <summary>A card was clicked/selected (interest, even if playback never starts).</summary>
    CardClicked = 24,

    /// <summary>A trailer preview actually played for the user (stronger interest than a click).</summary>
    TrailerPlayed = 25,

    /// <summary>A request was created (strong positive signal; feeds request affinity).</summary>
    RequestCreated = 30,

    /// <summary>A previously requested item became available.</summary>
    RequestFulfilled = 31
}
