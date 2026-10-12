using System;

namespace Wholphin.Engine.Data.Entities;

/// <summary>Durable per-user resume state, independent of torrent lifetime and library row ids.</summary>
public sealed class PlaybackProgressRecord
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "movie";
    // Zero is the movie sentinel; season zero is valid for TV specials.
    public int Season { get; set; }
    public int Episode { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public bool Completed { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long Revision { get; set; }
    public Guid? JellyfinItemId { get; set; }
    public long AppliedRevision { get; set; }
    public Guid ActiveSessionId { get; set; }
    public DateTime SessionStartedAt { get; set; }
    public DateTime LastNativeAt { get; set; }
    public long LastSequence { get; set; } = -1;
    public bool SessionStopped { get; set; }
}
