using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Configuration;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Http;
using Wholphin.Engine.Metadata;

namespace Wholphin.Engine.Trailer.Sources;

/// <summary>
/// Searches YouTube through yt-dlp and picks the best-scoring result. The only source that can cover
/// a title no metadata provider has a video for.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a blind <c>ytsearch1:</c> expression that was handed straight to the downloader
/// sight unseen — which is why the source shipped disabled. Fetching several candidates and scoring
/// them makes the difference between guessing and deciding: below the configured threshold this
/// returns null rather than a video it cannot vouch for.
/// </para>
/// <para>
/// No API key and no quota: it reuses the yt-dlp binary the trailer pipeline already requires, in one
/// metadata-only process call.
/// </para>
/// </remarks>
public class YtDlpSearchTrailerSource : ITrailerSource
{
    /// <summary>Metadata-only search; well under the download timeouts elsewhere in the pipeline.</summary>
    private const int SearchTimeoutMs = 30_000;

    /// <summary>Tab-separated so a title containing commas or pipes cannot break the parse.</summary>
    private const string PrintFormat = "%(id)s\\t%(title)s\\t%(channel)s\\t%(duration)s";

    private readonly IEngineMetrics _metrics;
    private readonly ILogger<YtDlpSearchTrailerSource> _logger;
    private readonly Func<PluginConfiguration?> _config;

    /// <summary>Initializes a new instance of the <see cref="YtDlpSearchTrailerSource"/> class.</summary>
    /// <param name="metrics">Operational metrics.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Configuration accessor; defaults to the live plugin configuration.</param>
    public YtDlpSearchTrailerSource(
        IEngineMetrics metrics,
        ILogger<YtDlpSearchTrailerSource> logger,
        Func<PluginConfiguration?>? config = null)
    {
        _metrics = metrics;
        _logger = logger;
        _config = config ?? (() => Plugin.Instance?.Configuration);
    }

    /// <inheritdoc />
    public string Name => "search";

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(MediaIdentity identity, string? preferredLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.Title))
        {
            return null;
        }

        var config = _config();
        var count = Math.Clamp(config?.TrailerSearchCandidates ?? 8, 1, 25);
        var minScore = config?.TrailerSearchMinScore ?? 40;
        var weights = TrailerScoreWeights.FromConfig(config);

        var query = BuildQuery(identity.Title, identity.Year, count);
        var stdout = await ExternalProcess.CaptureAsync(
            "yt-dlp",
            $"--skip-download --no-warnings --flat-playlist --print \"{PrintFormat}\" \"{query}\"",
            SearchTimeoutMs,
            _logger,
            cancellationToken).ConfigureAwait(false);

        var candidates = ParseLines(stdout);
        if (candidates.Count == 0)
        {
            return null;
        }

        var best = TrailerCandidateScorer.Best(candidates, identity.Title, identity.Year, weights, minScore);
        if (best is null)
        {
            // Candidates existed but none was convincing. Recorded separately from "found nothing":
            // a high rejection rate means the threshold needs tuning, not that YouTube is empty.
            _metrics.Increment("trailer.search.rejected");
            return null;
        }

        _metrics.Increment("trailer.search.picked");
        return "https://www.youtube.com/watch?v=" + best.Value.Id;
    }

    /// <summary>Builds the yt-dlp search expression.</summary>
    /// <param name="title">The media title.</param>
    /// <param name="year">The production year, when known.</param>
    /// <param name="count">How many results to ask for.</param>
    /// <returns>The search expression.</returns>
    internal static string BuildQuery(string title, int? year, int count)
    {
        // Quotes would be re-interpreted by the shell-less argument string; the scorer does the real
        // matching, so the query only has to get the right neighbourhood of results.
        var cleaned = title.Replace("\"", string.Empty, StringComparison.Ordinal).Trim();
        var suffix = year is > 0 ? " " + year.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        return $"ytsearch{count.ToString(CultureInfo.InvariantCulture)}:{cleaned}{suffix} official trailer";
    }

    /// <summary>
    /// Parses yt-dlp's tab-separated print output.
    /// </summary>
    /// <param name="stdout">The captured stdout, or null.</param>
    /// <returns>The candidates; malformed lines are skipped rather than failing the search.</returns>
    /// <remarks>
    /// yt-dlp prints "NA" for fields it has no value for, and flat-playlist output omits duration for
    /// some results — a missing duration must cost the candidate nothing rather than disqualify it.
    /// </remarks>
    internal static IReadOnlyList<TrailerCandidate> ParseLines(string? stdout)
    {
        var candidates = new List<TrailerCandidate>();
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return candidates;
        }

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || parts[0] == "NA")
            {
                continue;
            }

            candidates.Add(new TrailerCandidate(
                parts[0].Trim(),
                parts[1].Trim(),
                parts.Length > 2 ? Clean(parts[2]) : null,
                parts.Length > 3 ? ParseDuration(parts[3]) : null));
        }

        return candidates;
    }

    private static string? Clean(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 || trimmed == "NA" ? null : trimmed;
    }

    private static int? ParseDuration(string value)
        => double.TryParse(Clean(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? (int)seconds
            : null;
}
