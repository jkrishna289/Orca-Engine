namespace Wholphin.Engine.Playback;

/// <summary>Transfers durable stream progress to the user's matching library copy.</summary>
public interface IJellyfinProgressBridge
{
    Task ReconcileAsync(CancellationToken ct = default);

    Task ReconcileUserAsync(Guid userId, CancellationToken ct = default);

    Task ObserveExistingAsync(Guid userId, PlaybackIdentity identity, CancellationToken ct = default);
}
