using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Wholphin.Engine.Configuration;

namespace Wholphin.Engine.Trailer;

/// <summary>One YouTube search result, as yt-dlp prints it.</summary>
/// <param name="Id">The YouTube video id.</param>
/// <param name="Title">The video title.</param>
/// <param name="Channel">The uploading channel name.</param>
/// <param name="DurationSeconds">Duration in seconds, or null when yt-dlp did not report it.</param>
public readonly record struct TrailerCandidate(string Id, string Title, string? Channel, int? DurationSeconds);

/// <summary>The configurable weights behind trailer candidate scoring.</summary>
/// <remarks>
/// A record rather than constants so the whole model is one testable value and every weight is
/// admin-tunable — the scoring must not be scattered as magic numbers through the source that uses it.
/// </remarks>
public sealed record TrailerScoreWeights(
    int TitleMatch,
    int YearMatch,
    int OfficialChannel,
    int OfficialTrailer,
    int Trailer,
    int Unrelated,
    int FanMade,
    int Clip,
    int WrongYear)
{
    /// <summary>The shipped defaults.</summary>
    public static TrailerScoreWeights Default { get; } = new(40, 20, 20, 10, 5, 30, 20, 20, 50);

    /// <summary>Reads the weights from configuration, falling back to the defaults.</summary>
    /// <param name="config">The plugin configuration, or null.</param>
    /// <returns>The weights.</returns>
    public static TrailerScoreWeights FromConfig(PluginConfiguration? config) => config is null
        ? Default
        : new TrailerScoreWeights(
            config.TrailerScoreTitleMatch,
            config.TrailerScoreYearMatch,
            config.TrailerScoreOfficialChannel,
            config.TrailerScoreOfficialTrailer,
            config.TrailerScoreTrailer,
            config.TrailerScoreUnrelated,
            config.TrailerScoreFanMade,
            config.TrailerScoreClip,
            config.TrailerScoreWrongYear);
}

/// <summary>
/// Ranks YouTube search results so the <c>search</c> trailer source can be trusted.
/// </summary>
/// <remarks>
/// <para>
/// A blind "first search result" is why that source was off by default: a text search happily returns
/// a reaction video, a review, or a fan edit, and a confidently wrong trailer is worse than none.
/// Scoring plus a minimum threshold turns a guess into a decision that can DECLINE.
/// </para>
/// <para>
/// Separate from <c>TmdbClient.PickTrailer</c> on purpose. That ladder is right for TMDB's structured
/// video records, where type and official flags are data; this deals with unstructured free text.
/// </para>
/// </remarks>
public static class TrailerCandidateScorer
{
    /// <summary>A real trailer is not an 8-second clip nor a 40-minute breakdown.</summary>
    private const int MinDurationSeconds = 30;
    private const int MaxDurationSeconds = 360;

    /// <summary>Penalty for a duration outside the plausible band. Not configurable: a correctness gate, not taste.</summary>
    private const int DurationPenalty = 25;

    private static readonly string[] FanMadeMarkers =
    {
        "fan made", "fan-made", "fanmade", "fan trailer", "concept trailer", "concept teaser",
        "parody", "what if", "fan edit", "mashup", "remake trailer",
    };

    private static readonly string[] ClipMarkers =
    {
        "reaction", "review", "breakdown", "explained", "recap", "ending explained", "easter egg",
        "behind the scenes", "making of", "featurette", "interview", "bloopers", "deleted scene",
        "clip", "scene", "first look at", "everything wrong",
    };

    private static readonly string[] OfficialChannelMarkers =
    {
        "pictures", "studios", "entertainment", "films", "film", "movies", "netflix", "hbo", "max",
        "prime video", "disney", "marvel", "warner", "universal", "paramount", "sony", "lionsgate",
        "a24", "focus features", "mgm", "apple tv", "hulu", "peacock", "trailers",
    };

    /// <summary>
    /// Picks the best candidate, or null when none clears <paramref name="minScore"/>.
    /// </summary>
    /// <param name="candidates">The search results.</param>
    /// <param name="title">The media title being matched.</param>
    /// <param name="year">The production year, when known.</param>
    /// <param name="weights">The scoring weights.</param>
    /// <param name="minScore">The score a candidate must reach to be used at all.</param>
    /// <returns>The winning candidate, or null.</returns>
    public static TrailerCandidate? Best(
        IEnumerable<TrailerCandidate> candidates,
        string title,
        int? year,
        TrailerScoreWeights weights,
        int minScore)
    {
        TrailerCandidate? best = null;
        var bestScore = int.MinValue;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id))
            {
                continue;
            }

            var score = Score(candidate, title, year, weights);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        // Declining is the whole reason this is safe to enable by default.
        return bestScore >= minScore ? best : null;
    }

    /// <summary>Scores one candidate against the title it is supposed to be a trailer for.</summary>
    /// <param name="candidate">The search result.</param>
    /// <param name="title">The media title.</param>
    /// <param name="year">The production year, when known.</param>
    /// <param name="weights">The scoring weights.</param>
    /// <returns>The score; higher is better.</returns>
    public static int Score(TrailerCandidate candidate, string title, int? year, TrailerScoreWeights weights)
    {
        var videoTitle = Normalize(candidate.Title);
        var wanted = Normalize(title);
        var score = 0;

        if (!string.IsNullOrEmpty(wanted) && videoTitle.Contains(wanted, StringComparison.Ordinal))
        {
            score += weights.TitleMatch;
        }
        else
        {
            score -= weights.Unrelated;
        }

        if (videoTitle.Contains("official trailer", StringComparison.Ordinal))
        {
            score += weights.OfficialTrailer;
        }
        else if (videoTitle.Contains("trailer", StringComparison.Ordinal) || videoTitle.Contains("teaser", StringComparison.Ordinal))
        {
            score += weights.Trailer;
        }

        if (LooksOfficial(candidate.Channel, wanted))
        {
            score += weights.OfficialChannel;
        }

        if (FanMadeMarkers.Any(m => videoTitle.Contains(m, StringComparison.Ordinal)))
        {
            score -= weights.FanMade;
        }

        if (ClipMarkers.Any(m => videoTitle.Contains(m, StringComparison.Ordinal)))
        {
            score -= weights.Clip;
        }

        score += YearScore(candidate.Title, year, weights);

        // A trailer has a shape. This catches the 12-second teaser fragment and the hour-long
        // "everything we know" video that survive every text signal above.
        if (candidate.DurationSeconds is { } duration && (duration < MinDurationSeconds || duration > MaxDurationSeconds))
        {
            score -= DurationPenalty;
        }

        return score;
    }

    /// <summary>
    /// Scores the year signal: a matching year is evidence, a year two or more off is near-fatal.
    /// </summary>
    /// <remarks>
    /// Only 4-digit years in a plausible film range count. Resolutions ("1080p"), episode counts and
    /// view counts would otherwise read as years and poison the signal.
    /// </remarks>
    private static int YearScore(string videoTitle, int? year, TrailerScoreWeights weights)
    {
        if (year is not > 0)
        {
            return 0;
        }

        var years = ExtractYears(videoTitle);
        if (years.Count == 0)
        {
            return 0;
        }

        if (years.Contains(year.Value))
        {
            return weights.YearMatch;
        }

        // A re-release or a sequel's marketing may sit a year either side; two or more is a different film.
        return years.Any(y => Math.Abs(y - year.Value) >= 2) ? -weights.WrongYear : 0;
    }

    /// <summary>Extracts plausible 4-digit release years from a video title.</summary>
    /// <param name="text">The title.</param>
    /// <returns>The years found.</returns>
    internal static IReadOnlyCollection<int> ExtractYears(string? text)
    {
        var years = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return years;
        }

        for (var i = 0; i + 4 <= text.Length; i++)
        {
            // Must be a standalone 4-digit run, so "1080p" and "24000" never read as a year.
            if (!char.IsDigit(text[i])
                || (i > 0 && char.IsDigit(text[i - 1]))
                || (i + 4 < text.Length && char.IsDigit(text[i + 4])))
            {
                continue;
            }

            var slice = text.AsSpan(i, 4);
            if (!int.TryParse(slice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            if (value is >= 1900 and <= 2100)
            {
                years.Add(value);
            }
        }

        return years;
    }

    /// <summary>
    /// Whether a channel looks like a studio or distributor rather than an individual.
    /// </summary>
    /// <remarks>
    /// A channel named after the title itself counts too: franchise channels ("The Witcher") are
    /// official even though they carry no studio word.
    /// </remarks>
    private static bool LooksOfficial(string? channel, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return false;
        }

        var normalized = Normalize(channel);
        return OfficialChannelMarkers.Any(m => normalized.Contains(m, StringComparison.Ordinal))
               || (!string.IsNullOrEmpty(normalizedTitle) && normalized.Contains(normalizedTitle, StringComparison.Ordinal));
    }

    /// <summary>Lowercases and strips punctuation so "Spider-Man: No Way Home" matches "spider man no way home".</summary>
    /// <param name="text">The raw text.</param>
    /// <returns>The normalized form.</returns>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
