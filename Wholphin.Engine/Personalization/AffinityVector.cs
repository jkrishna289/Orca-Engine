using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// A user's multi-dimensional taste profile. Each dimension maps a feature value
/// (e.g. a genre name) to a normalized affinity score in roughly [-1, 1] — positive
/// means the user gravitates toward it, negative means they avoid it. Serialized to
/// <see cref="Data.Entities.UserProfile.AffinityJson"/>.
/// </summary>
public class AffinityVector
{
    /// <summary>Gets or sets genre affinities (e.g. "Science Fiction" → 0.92).</summary>
    public Dictionary<string, double> Genre { get; set; } = new();

    /// <summary>Gets or sets studio affinities.</summary>
    public Dictionary<string, double> Studio { get; set; } = new();

    /// <summary>Gets or sets tag/keyword affinities.</summary>
    public Dictionary<string, double> Tag { get; set; } = new();

    /// <summary>Gets or sets decade affinities (e.g. "1990" → 0.7).</summary>
    public Dictionary<string, double> Decade { get; set; } = new();

    /// <summary>Gets or sets media-type affinities (e.g. "Movie" → 0.8).</summary>
    public Dictionary<string, double> MediaType { get; set; } = new();

    /// <summary>Gets or sets people affinities, keyed by role-prefixed name (e.g. "Director:Christopher Nolan" → 0.9).</summary>
    public Dictionary<string, double> Person { get; set; } = new();

    /// <summary>Gets or sets runtime-bucket affinities (keys: short/medium/long/epic).</summary>
    public Dictionary<string, double> Runtime { get; set; } = new();

    /// <summary>Gets or sets maturity-rating affinities, keyed by official rating (e.g. "PG-13" → 0.6).</summary>
    public Dictionary<string, double> Maturity { get; set; } = new();

    /// <summary>Gets or sets language affinities, keyed by ISO-639-1 code (e.g. "hi" → 0.8).</summary>
    public Dictionary<string, double> Language { get; set; } = new();

    /// <summary>Gets or sets franchise/collection affinities, keyed by collection name.</summary>
    public Dictionary<string, double> Franchise { get; set; } = new();

    /// <summary>
    /// Gets or sets the confidence score (0-1) scaling with how much behavior data backs this
    /// profile. Below ~0.40 the recommender should lean on global priors (cold-start).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>Gets or sets the number of weighted behavior events folded in.</summary>
    public int EventCount { get; set; }

    /// <summary>Gets or sets when this vector was computed (UTC).</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>
    /// Gets or sets per-daypart affinity sub-vectors (keys: Morning/Afternoon/Evening/Night),
    /// each a slimmed <see cref="AffinityVector"/> (its own <see cref="Dayparts"/> stays empty).
    /// Stored inline in the same JSON so context-awareness needs no schema change. Empty on the
    /// blended vector the recommender consumes.
    /// </summary>
    public Dictionary<string, AffinityVector> Dayparts { get; set; } = new();
}
