using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Wholphin.Engine.Streaming;

/// <summary>A single file inside a torrent, reduced to what source selection needs.</summary>
/// <param name="Path">The file's path within the torrent (may contain directories).</param>
/// <param name="Length">The file's size in bytes.</param>
public sealed record TorrentFileEntry(string Path, long Length);

/// <summary>
/// Picks the one file in a torrent that should actually be played.
///
/// A release rarely contains a single file — samples, trailers, featurettes, artwork and subtitle
/// sidecars ride along, and a season pack holds a dozen episodes. Playing the wrong one is the most
/// visible way this feature fails, so the rules are deterministic and ordered rather than clever:
/// extension, then name, then a size floor, then the episode/largest tiebreak.
///
/// Pure and side-effect free by design — this is the piece that carries the unit tests.
/// </summary>
public static class VideoFileSelector
{
    // Containers worth attempting. Anything else (nfo, srt, jpg, exe) is not a playback candidate.
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".webm", ".mov",
    };

    // Names that mark a file as an accompaniment rather than the feature. Matched against the whole
    // path, because releases just as often bury these in a "Sample/" or "Extras/" directory as they
    // do in the filename.
    private static readonly Regex JunkPattern = new(
        @"sample|trailer|preview|extras?|featurette|behind[\s._-]*the[\s._-]*scenes|deleted[\s._-]*scene",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Season/episode tag, tolerant of the usual separators: S01E02, s1.e2, S01 E02.
    private static readonly Regex EpisodePattern = new(
        @"[Ss](\d{1,2})[\s._-]*[Ee](\d{1,3})",
        RegexOptions.Compiled);

    // Samples that dodge the name filter are still tiny next to the feature. A real episode in a
    // season pack is never a tenth of the largest episode, so this floor is safe for packs too.
    private const double MinFractionOfLargest = 0.10;

    /// <summary>
    /// Returns the file that should be streamed, or null when the torrent holds no video at all.
    /// </summary>
    /// <param name="files">Every file in the torrent.</param>
    /// <param name="season">Target season, when a specific episode is wanted.</param>
    /// <param name="episode">Target episode, when a specific episode is wanted.</param>
    public static TorrentFileEntry? Select(
        IReadOnlyList<TorrentFileEntry> files,
        int? season = null,
        int? episode = null)
    {
        if (files is null || files.Count == 0)
        {
            return null;
        }

        // 1. Only real containers.
        var videos = files
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f.Path)))
            .ToList();

        if (videos.Count == 0)
        {
            return null;
        }

        // 2. Drop accompaniments. If that empties the list the release names the feature itself
        //    something like "Extras" — falling back beats returning nothing, since "no video found"
        //    on a torrent that plainly has one is the worse failure.
        var candidates = videos.Where(f => !JunkPattern.IsMatch(f.Path)).ToList();
        if (candidates.Count == 0)
        {
            candidates = videos;
        }

        // 3. Size floor, relative to the biggest survivor.
        var largest = candidates.Max(f => f.Length);
        var threshold = (long)(largest * MinFractionOfLargest);
        candidates = candidates.Where(f => f.Length >= threshold).ToList();

        // 4. A requested episode wins outright; otherwise the biggest file is the feature.
        if (season.HasValue && episode.HasValue)
        {
            var match = candidates.FirstOrDefault(f => MatchesEpisode(f.Path, season.Value, episode.Value));
            if (match is not null)
            {
                return match;
            }
        }

        return candidates.OrderByDescending(f => f.Length).First();
    }

    private static bool MatchesEpisode(string path, int season, int episode)
    {
        foreach (Match m in EpisodePattern.Matches(path))
        {
            if (int.TryParse(m.Groups[1].Value, out var s)
                && int.TryParse(m.Groups[2].Value, out var e)
                && s == season
                && e == episode)
            {
                return true;
            }
        }

        return false;
    }
}
