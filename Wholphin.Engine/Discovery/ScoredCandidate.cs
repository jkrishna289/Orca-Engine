namespace Wholphin.Engine.Discovery;

/// <summary>A candidate paired with its score breakdown — the unit the ranking stages pass along.</summary>
/// <param name="Candidate">The candidate.</param>
/// <param name="Score">Its score breakdown (mutated only by the diversity stage's penalty).</param>
public sealed record ScoredCandidate(DiscoveryCandidate Candidate, DiscoveryScore Score);
