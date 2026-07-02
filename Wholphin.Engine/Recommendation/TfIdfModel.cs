using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// A pure, deterministic TF-IDF vector-space model built from a corpus of token documents.
/// Captures <em>thematic</em> content similarity (rare shared terms weigh more) to complement the
/// flat set-overlap of <see cref="SimilarityScorer"/>. Stateless after construction and
/// unit-testable in isolation — no I/O, no framework types.
/// </summary>
public sealed class TfIdfModel
{
    private static readonly IReadOnlyDictionary<string, double> EmptyVector =
        new Dictionary<string, double>();

    private readonly Dictionary<string, double> _idf;

    private TfIdfModel(Dictionary<string, double> idf, int documentCount)
    {
        _idf = idf;
        DocumentCount = documentCount;
    }

    /// <summary>Gets the number of documents the model was trained on.</summary>
    public int DocumentCount { get; }

    /// <summary>Gets the size of the learned vocabulary (distinct terms).</summary>
    public int VocabularySize => _idf.Count;

    /// <summary>
    /// Builds the model by computing the inverse document frequency of every term in the corpus.
    /// </summary>
    /// <param name="corpus">The training documents, each a collection of (possibly repeated) terms.</param>
    /// <returns>The trained model.</returns>
    public static TfIdfModel Build(IEnumerable<IReadOnlyCollection<string>> corpus)
    {
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        var documentCount = 0;

        foreach (var document in corpus)
        {
            documentCount++;

            // Count each term once per document (document frequency, not term frequency).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var term in document)
            {
                if (seen.Add(term))
                {
                    documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
                }
            }
        }

        var idf = new Dictionary<string, double>(documentFrequency.Count, StringComparer.Ordinal);
        foreach (var (term, df) in documentFrequency)
        {
            // Smoothed IDF: ln(1 + N/df). Always positive; rarer terms (low df) weigh more,
            // a term in every document approaches 0.
            idf[term] = Math.Log(1.0 + (documentCount / (double)df));
        }

        return new TfIdfModel(idf, documentCount);
    }

    /// <summary>
    /// Projects a document into an L2-normalized TF-IDF vector. Terms outside the learned
    /// vocabulary are ignored, so unseen content contributes nothing rather than skewing the norm.
    /// </summary>
    /// <param name="tokens">The document's terms (repeats are meaningful — they raise term frequency).</param>
    /// <returns>A sparse term→weight map whose vector length is 1 (empty when no in-vocabulary terms).</returns>
    public IReadOnlyDictionary<string, double> Vectorize(IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return EmptyVector;
        }

        var termFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in tokens)
        {
            if (_idf.ContainsKey(term))
            {
                termFrequency[term] = termFrequency.GetValueOrDefault(term) + 1;
            }
        }

        if (termFrequency.Count == 0)
        {
            return EmptyVector;
        }

        var terms = new string[termFrequency.Count];
        var weights = new double[termFrequency.Count];
        var sumSquares = 0.0;
        var index = 0;
        foreach (var (term, count) in termFrequency)
        {
            // Sublinear term frequency (1 + ln tf) dampens repeated words; scaled by IDF.
            var weight = (1.0 + Math.Log(count)) * _idf[term];
            terms[index] = term;
            weights[index] = weight;
            sumSquares += weight * weight;
            index++;
        }

        if (sumSquares <= 0)
        {
            return EmptyVector;
        }

        var norm = Math.Sqrt(sumSquares);
        var vector = new Dictionary<string, double>(terms.Length, StringComparer.Ordinal);
        for (var i = 0; i < terms.Length; i++)
        {
            vector[terms[i]] = weights[i] / norm;
        }

        return vector;
    }

    /// <summary>
    /// Cosine similarity of two L2-normalized vectors (i.e. their dot product), clamped to [0, 1].
    /// </summary>
    /// <param name="a">The first vector (from <see cref="Vectorize"/>).</param>
    /// <param name="b">The second vector (from <see cref="Vectorize"/>).</param>
    /// <returns>The cosine similarity in [0, 1] (0 when either vector is empty).</returns>
    public static double Cosine(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0.0;
        }

        // Iterate the smaller vector for efficiency; both are already unit-length.
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);

        var dot = 0.0;
        foreach (var (term, weight) in small)
        {
            if (large.TryGetValue(term, out var other))
            {
                dot += weight * other;
            }
        }

        return Math.Clamp(dot, 0.0, 1.0);
    }
}
