using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Builds and serves per-user affinity vectors from the behavior log.
/// </summary>
public interface IPersonalizationService
{
    /// <summary>
    /// Recomputes a user's affinity vector from their full behavior history, applying
    /// signal weights, exponential time-decay, normalization, and a confidence score,
    /// then persists it to the user's profile.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The freshly computed affinity vector.</returns>
    Task<AffinityVector> RecomputeAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a user's stored affinity vector, or an empty one if none has been computed.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored (or empty) affinity vector.</returns>
    Task<AffinityVector> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the user's affinity blended toward the current daypart's taste (context-aware).
    /// The blend weight scales with the daypart's own confidence, so sparse dayparts fall back to
    /// the all-time vector (cold-start safe). The overall confidence is preserved for ranking gates.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="hourUtc">The hour (UTC) to contextualize for; null uses the current hour.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The daypart-blended affinity vector.</returns>
    Task<AffinityVector> GetContextualAsync(Guid userId, int? hourUtc, CancellationToken ct = default);

    /// <summary>
    /// Returns the effective ranking vector: the daypart-adjusted long-term taste pulled toward the
    /// user's current session (last few items watched within the session window). Falls back to the
    /// daypart vector when there's no recent activity. This is what the recommender should consume.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="hourUtc">The hour (UTC) to contextualize for; null uses the current hour.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The daypart- and session-blended affinity vector.</returns>
    Task<AffinityVector> GetEffectiveAsync(Guid userId, int? hourUtc, CancellationToken ct = default);
}
