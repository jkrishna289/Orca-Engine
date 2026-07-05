using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Intelligence;

/// <summary>
/// A per-user intent scorer bound to a single affinity snapshot, so a batch of candidates can be
/// scored without re-fetching the profile. This is the engine's one "IntentScore" surface — it wraps
/// the existing content-affinity math (<see cref="CatalogFeatures.Dot"/>) rather than inventing a
/// parallel model.
/// </summary>
public sealed class IntentScorer
{
    /// <summary>Franchise affinity above which the user is treated as "following" the franchise.</summary>
    public const double FranchiseFollowThreshold = 0.1;

    private readonly AffinityVector _affinity;

    /// <summary>Initializes a new instance of the <see cref="IntentScorer"/> class.</summary>
    /// <param name="affinity">The user's affinity snapshot (empty for anonymous/cold).</param>
    public IntentScorer(AffinityVector affinity) => _affinity = affinity;

    /// <summary>Gets the profile confidence (0 when anonymous/cold).</summary>
    public double Confidence => _affinity.Confidence;

    /// <summary>Scores an item's taste alignment (unbounded; positive = aligned, negative = avoided).</summary>
    /// <param name="item">The catalog item.</param>
    /// <returns>The affinity dot score.</returns>
    public double Score(CatalogItem item) => CatalogFeatures.Dot(_affinity, item);

    /// <summary>Returns whether the user positively follows the item's franchise/collection.</summary>
    /// <param name="item">The catalog item.</param>
    /// <returns>True when the franchise affinity clears <see cref="FranchiseFollowThreshold"/>.</returns>
    public bool FollowsFranchise(CatalogItem item)
        => !string.IsNullOrWhiteSpace(item.CollectionName)
            && _affinity.Franchise.GetValueOrDefault(item.CollectionName!) > FranchiseFollowThreshold;
}
