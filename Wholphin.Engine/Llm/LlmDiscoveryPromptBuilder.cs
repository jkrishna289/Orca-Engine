using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Llm;

/// <summary>One watch-history line sent to the model: a title and (when known) its release year.</summary>
/// <param name="Title">The display title.</param>
/// <param name="Year">The release year, when known.</param>
public sealed record HistoryLine(string Title, int? Year);

/// <summary>
/// Builds the LLM-discovery prompts: the model is asked to act as the candidate GENERATOR
/// (proposing titles from its own knowledge of film/TV), not a re-ranker — that open-world step is
/// what TMDB's /similar graph can't do. The payload is richer than a flat "recently watched" list:
/// loved vs merely-watched titles, explicit dislikes as negative constraints, and the affinity
/// profile's genre/people signals. Anonymized by construction — titles and public metadata only,
/// never account identity. Pure static.
/// </summary>
public static partial class LlmDiscoveryPromptBuilder
{
    /// <summary>The system prompt for candidate generation.</summary>
    public const string SystemPrompt =
        "You are a media recommendation engine for one anonymous viewer of a personal media server. "
        + "You output ONLY a raw JSON object — no markdown fences, no prose, no explanations outside the JSON.";

    /// <summary>The corrective system message injected when a reply fails to parse (SuggestArr's retry pattern).</summary>
    public const string CorrectiveSystemPrompt =
        "Your previous response was not the required raw JSON object. Reply again with ONLY the JSON "
        + "object matching the requested schema — no markdown fences, no commentary.";

    /// <summary>Builds the per-media-type user prompt from anonymized, pre-capped context data.</summary>
    /// <param name="mediaType">Movie or Series (one call per type, per the SuggestArr pattern).</param>
    /// <param name="maxResults">How many recommendations to ask for.</param>
    /// <param name="loved">Titles with strong positive signals (thumbs up / favorite territory).</param>
    /// <param name="watched">Titles watched to completion without a strong explicit signal.</param>
    /// <param name="disliked">Titles the viewer explicitly rejected (negative constraints).</param>
    /// <param name="topGenres">The profile's strongest genres.</param>
    /// <param name="avoidGenres">Genres the viewer actively avoids (hard veto).</param>
    /// <param name="topPeople">The viewer's strongest people affinities (role prefix stripped).</param>
    /// <returns>The user prompt.</returns>
    public static string Build(
        MediaType mediaType,
        int maxResults,
        IReadOnlyList<HistoryLine> loved,
        IReadOnlyList<HistoryLine> watched,
        IReadOnlyList<HistoryLine> disliked,
        IReadOnlyList<string> topGenres,
        IReadOnlyList<string> avoidGenres,
        IReadOnlyList<string> topPeople)
    {
        var noun = mediaType == MediaType.Series ? "TV series" : "movies";
        var singular = mediaType == MediaType.Series ? "TV series" : "movie";

        var sb = new StringBuilder();
        sb.Append("Recommend exactly ").Append(maxResults).Append(' ').Append(noun)
            .AppendLine(" for this viewer, based on their taste. Analyze the themes, genres, pacing")
            .AppendLine("and tone of what they loved to build a taste profile before choosing.")
            .AppendLine()
            .AppendLine("Viewer taste (anonymized):");
        AppendTitles(sb, "Loved", loved);
        AppendTitles(sb, "Watched and finished", watched);
        AppendTitles(sb, "Disliked — do NOT recommend anything similar to these", disliked);
        AppendNames(sb, "Favorite genres", topGenres);
        AppendNames(sb, "Never recommend these genres", avoidGenres);
        AppendNames(sb, "Likes work by", topPeople);

        sb.AppendLine()
            .AppendLine("Strict rules:")
            .AppendLine("1. Do NOT recommend any title listed above, nor direct sequels/prequels of titles the viewer has clearly already seen.")
            .Append("2. Every recommendation must be a real ").Append(singular)
            .AppendLine(" that exists on TMDB; use its official title (the English title when one exists) with no extra text.")
            .AppendLine("3. Respond with ONLY a valid JSON object of this exact shape:")
            .AppendLine("{\"recommendations\":[{\"title\":\"<official title>\",\"year\":<first release year, integer>,")
            .AppendLine("\"rationale\":\"<one short sentence, max 100 characters, why it fits this viewer>\",")
            .AppendLine("\"source_title\":\"<the EXACT title from the lists above that most inspired this pick, or \\\"\\\">\"}]}");

        return sb.ToString();
    }

    /// <summary>
    /// SuggestArr's title normalization: strips episode notation ("Dark - S02E12", "Dark S02E12")
    /// and trailing year suffixes ("Dune (2021)") so history dedupe and source-title matching
    /// compare the bare title.
    /// </summary>
    /// <param name="title">The raw title.</param>
    /// <returns>The normalized title.</returns>
    public static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var text = EpisodeNotation().Replace(title, string.Empty);
        text = YearSuffix().Replace(text, string.Empty);
        return text.Trim().TrimEnd('-', '–', ':').Trim();
    }

    [GeneratedRegex(@"\s*[-–:]?\s*S\d{1,2}\s*E\d{1,3}.*$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNotation();

    [GeneratedRegex(@"\s*\((19|20)\d{2}\)\s*$")]
    private static partial Regex YearSuffix();

    private static void AppendTitles(StringBuilder sb, string label, IReadOnlyList<HistoryLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        sb.Append("- ").Append(label).Append(": ");
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(lines[i].Title);
            if (lines[i].Year is { } year)
            {
                sb.Append(" (").Append(year).Append(')');
            }
        }

        sb.AppendLine();
    }

    private static void AppendNames(StringBuilder sb, string label, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }

        sb.Append("- ").Append(label).Append(": ").AppendLine(string.Join(", ", names));
    }
}
