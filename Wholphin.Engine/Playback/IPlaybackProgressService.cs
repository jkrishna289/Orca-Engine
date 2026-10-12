using Wholphin.Engine.Data.Entities;
using System.Text.Json.Serialization;

namespace Wholphin.Engine.Playback;

public sealed record PlaybackIdentity(int TmdbId, string MediaType, int Season = 0, int Episode = 0)
{
    public PlaybackIdentity Validate()
    {
        var normalized = MediaType?.Trim().ToLowerInvariant();
        if (TmdbId <= 0 || normalized is not ("movie" or "tv") ||
            (normalized == "movie" && (Season != 0 || Episode != 0)) ||
            (normalized == "tv" && (Season < 0 || Episode <= 0)))
        {
            throw new ArgumentException("Supply a positive TMDB id and movie, or tv with season and episode.");
        }

        return this with { MediaType = normalized };
    }
}

public sealed record PlaybackProgressUpdate(
    [property: JsonPropertyName("Sequence")] long Sequence,
    [property: JsonPropertyName("PositionMs")] long PositionMs,
    [property: JsonPropertyName("DurationMs")] long DurationMs,
    [property: JsonPropertyName("Completed")] bool Completed,
    [property: JsonPropertyName("Stopped")] bool Stopped);

public enum PlaybackReportResult { Accepted, Duplicate, NotFound, Superseded }

public interface IPlaybackProgressService
{
    Task<PlaybackProgressRecord> StartAsync(Guid userId, PlaybackIdentity identity, string? title = null, int? year = null, CancellationToken ct = default);
    Task<PlaybackReportResult> ReportAsync(Guid userId, Guid sessionId, PlaybackProgressUpdate update, CancellationToken ct = default);
    Task<PlaybackProgressRecord?> GetAsync(Guid userId, PlaybackIdentity identity, CancellationToken ct = default);
    Task<IReadOnlyList<PlaybackProgressRecord>> GetRecentAsync(Guid userId, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<PlaybackProgressRecord>> GetPendingAsync(CancellationToken ct = default);
    /// <summary>Serializes a Jellyfin apply/adoption with progress writes. Callback must not reenter this service.</summary>
    Task ReconcileAsync(long recordId, Func<PlaybackProgressRecord, CancellationToken, Task<bool>> apply, CancellationToken ct = default);
    Task ObserveLocalAsync(Guid userId, PlaybackIdentity identity, Guid jellyfinItemId, long positionMs, long durationMs, bool completed, DateTime observedAt, CancellationToken ct = default);
}
