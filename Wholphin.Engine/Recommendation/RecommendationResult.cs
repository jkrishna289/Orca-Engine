using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// A scored recommendation: the catalog item plus its final score and the component
/// contributions that produced it (for ranking transparency / "why recommended").
/// </summary>
public class RecommendationResult
{
    /// <summary>Initializes a new instance of the <see cref="RecommendationResult"/> class.</summary>
    /// <param name="item">The recommended catalog item.</param>
    public RecommendationResult(CatalogItem item) => Item = item;

    /// <summary>Gets the recommended catalog item.</summary>
    public CatalogItem Item { get; }

    /// <summary>Gets or sets the final blended score (higher = stronger recommendation).</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets the personalization component (0-1, after candidate-set normalization).</summary>
    public double Personalization { get; set; }

    /// <summary>Gets or sets the content-quality component (0-1, from community rating).</summary>
    public double Quality { get; set; }

    /// <summary>Gets or sets the recency component (0-1, from date added).</summary>
    public double Recency { get; set; }

    /// <summary>Gets or sets the availability component (0-1; full for instantly playable).</summary>
    public double Availability { get; set; }
}
