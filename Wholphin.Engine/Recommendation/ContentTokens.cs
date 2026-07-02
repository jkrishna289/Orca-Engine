using System;
using System.Collections.Generic;
using System.Text;

namespace Wholphin.Engine.Recommendation;

/// <summary>
/// Tokenizes a text document into a bag of word terms for the <see cref="TfIdfModel"/>: lower-cased,
/// split on non-alphanumeric runs, with very short words and common stop-words dropped. Pure and
/// deterministic.
/// </summary>
public static class ContentTokens
{
    // Common English words that carry no thematic signal. Words shorter than 3 chars are dropped
    // outright (covers a/an/of/to/in/is/it/...), so this only needs the longer stop-words.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "her",
        "was", "one", "our", "out", "his", "has", "him", "how", "its", "she", "two", "who",
        "did", "get", "let", "put", "say", "too", "use", "way", "this", "that", "they",
        "them", "then", "than", "from", "have", "were", "what", "when", "will", "your", "with",
        "into", "more", "only", "over", "such", "some", "also", "been", "being", "very", "just",
        "like", "while", "where", "which", "after", "their", "there", "would", "could", "should",
        "about", "other", "these", "those", "much", "many", "most", "make", "made", "back",
        "even", "find", "gets", "goes", "must", "near", "next", "once", "onto",
        "still", "take", "takes", "tell", "turn", "upon", "using", "until", "based",
    };

    /// <summary>Splits text into significant lower-cased word terms.</summary>
    /// <param name="text">The text to tokenize.</param>
    /// <returns>The word terms (repeats preserved so term frequency is meaningful).</returns>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var word = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                word.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush(tokens, word);
            }
        }

        Flush(tokens, word);
        return tokens;
    }

    private static void Flush(List<string> tokens, StringBuilder word)
    {
        if (word.Length == 0)
        {
            return;
        }

        var value = word.ToString();
        word.Clear();

        if (value.Length < 3 || StopWords.Contains(value))
        {
            return;
        }

        tokens.Add(value);
    }
}
