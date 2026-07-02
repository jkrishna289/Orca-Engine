using System;

namespace Wholphin.Engine.Data.Entities;

/// <summary>
/// Tracks incremental-sync progress (cursor/watermark) per sync source.
/// </summary>
public class SyncState
{
    /// <summary>Gets or sets the sync source id (e.g., "library").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the last processed cursor (e.g., a timestamp in ticks).</summary>
    public long LastCursorTicks { get; set; }

    /// <summary>Gets or sets when this state was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
