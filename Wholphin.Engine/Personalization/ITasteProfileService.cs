using System;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Builds and persists the per-user taste embedding file (one JSON document per user under
/// <c>{DataPath}/wholphin-engine/profiles/</c>) and re-derives the user's taste vector inside any
/// content-vector snapshot for same-space cosine comparisons.
/// </summary>
public interface ITasteProfileService
{
    /// <summary>Reads a user's persisted taste profile.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The profile, or null when none has been built (or the file is unreadable).</returns>
    Task<UserTasteProfile?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds a user's taste profile from their behavior events and the current content-vector
    /// snapshot, then persists it to the profile file.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rebuilt profile (empty seeds/vector when the user has no positive history).</returns>
    Task<UserTasteProfile> RebuildAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Re-derives the user's taste vector inside the supplied snapshot's embedding space — the
    /// weighted mean of the seed items' vectors. This, not the persisted vector, is the safe way
    /// to compare a user against snapshot items when the provider is sparse (TF-IDF is fit per
    /// batch, so persisted sparse vectors are only valid within the snapshot they came from).
    /// </summary>
    /// <param name="profile">The user's taste profile.</param>
    /// <param name="snapshot">The snapshot to project into.</param>
    /// <returns>The user vector, or <see cref="ContentVector.Empty"/> when no seed is indexed.</returns>
    ContentVector VectorInSnapshotSpace(UserTasteProfile profile, ContentVectorSnapshot snapshot);
}
