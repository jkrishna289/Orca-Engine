using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Wholphin.Engine.Streaming;

/// <summary>
/// Turns a pile of indexer results into the few choices a viewer is actually offered.
///
/// Two jobs. First, read the release name — scene naming is the only metadata public indexers give
/// us, so resolution, tier, codec, HDR and audio all come from parsing the title. Second, score and
/// bucket, because a list of eighty near-identical releases is not a choice.
///
/// The bias throughout is toward *starting playback successfully*, not toward maximum fidelity: a
/// 60GB remux with four seeders is a worse experience than a 6GB WEB-DL with nine hundred, and the
/// scoring says so. Pure and deterministic — this is the piece that carries the unit tests.
/// </summary>
public static class SourceRanker
{
    // Enough choice to matter, few enough that "Show all sources" stays navigable on a remote.
    private const int MaxResults = 20;

    // Below this a stream realistically stalls, so it is not offered at all.
    private const int MinSeeders = 2;

    // The seeder count past which more peers stop improving playback — beyond here the bottleneck is
    // the viewer's own connection, not the swarm.
    private const int SaturationSeeders = 100;

    // Weight of swarm health at saturation. Deliberately the largest single term: a stream that
    // starts beats a stream that looks better on paper.
    private const double MaxHealthPoints = 40;

    private static readonly Regex ResolutionPattern = new(
        @"\b(?:(?<h>480|540|576|720|1080|1440|2160|4320)[pi]|(?<uhd>4k|uhd))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GroupPattern = new(
        @"-(?<group>[A-Za-z0-9._]{2,20})\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Ranks and groups raw indexer results.
    /// </summary>
    /// <param name="candidates">Parsed-but-unscored sources (title, size, seeders, magnet filled in).</param>
    /// <param name="preferredHeight">
    /// The display's vertical resolution, so scoring can prefer a match. 1080 keeps a 1080p release
    /// ahead of a 4K one on a 1080p screen, where the extra bitrate buys nothing and costs startup time.
    /// </param>
    /// <returns>The grouped result.</returns>
    public static SourceGroups Rank(IEnumerable<TorrentSource> candidates, int preferredHeight = 1080)
    {
        var parsed = (candidates ?? Enumerable.Empty<TorrentSource>())
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.DownloadUrl))
            .Select(Describe)
            // A dead torrent is not a source, however good the release looks.
            .Where(c => c.Seeders >= MinSeeders)
            // CAMs are never worth offering; showing one would make the feature look broken.
            .Where(c => c.Tier != ReleaseTier.Cam)
            .ToList();

        var deduped = Dedupe(parsed);

        foreach (var s in deduped)
        {
            s.Score = ScoreOf(s, preferredHeight);
        }

        var ranked = deduped
            .OrderByDescending(s => s.Score)
            .Take(MaxResults)
            .ToList();

        if (ranked.Count == 0)
        {
            return new SourceGroups();
        }

        return new SourceGroups
        {
            Recommended = ranked[0],
            BestQuality = ranked
                .OrderByDescending(s => s.ResolutionHeight)
                .ThenByDescending(s => (int)s.Tier)
                .ThenByDescending(s => s.Seeders)
                .First(),
            FastestStart = ranked.OrderByDescending(s => s.Seeders).First(),
            LowestBandwidth = ranked
                .Where(s => s.SizeBytes > 0)
                .OrderBy(s => s.SizeBytes)
                .FirstOrDefault() ?? ranked[^1],
            FourKHdr = ranked
                .Where(s => s.ResolutionHeight >= 2160 && (s.Hdr || s.DolbyVision))
                .OrderByDescending(s => s.Score)
                .FirstOrDefault(),
            All = ranked,
        };
    }

    /// <summary>
    /// Fills in everything derivable from a release name, plus the viewer-facing wording.
    /// </summary>
    /// <param name="source">The source to describe; mutated and returned.</param>
    /// <returns>The same instance, populated.</returns>
    public static TorrentSource Describe(TorrentSource source)
    {
        var title = source.Title ?? string.Empty;

        source.ResolutionHeight = ParseResolution(title);
        source.Tier = ParseTier(title);
        source.VideoCodec = ParseVideoCodec(title);
        source.DolbyVision = Regex.IsMatch(title, @"\b(?:dv|dovi|dolby[\s._-]?vision)\b", RegexOptions.IgnoreCase);
        source.Hdr = source.DolbyVision
            || Regex.IsMatch(title, @"\b(?:hdr10\+?|hdr|hlg|pq)\b", RegexOptions.IgnoreCase);
        source.Audio = ParseAudio(title);
        source.ReleaseGroup = ParseGroup(title);
        source.Quality = QualityWord(source);
        source.Summary = BuildSummary(source);
        return source;
    }

    private static int ParseResolution(string title)
    {
        var m = ResolutionPattern.Match(title);
        if (!m.Success)
        {
            return 0;
        }

        if (m.Groups["uhd"].Success)
        {
            return 2160;
        }

        return int.TryParse(m.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;
    }

    private static ReleaseTier ParseTier(string title)
    {
        // Order matters: "BluRay REMUX" must read as Remux, and "WEBRip" must not match "WEB-DL".
        if (Regex.IsMatch(title, @"\bremux\b", RegexOptions.IgnoreCase))
        {
            return ReleaseTier.Remux;
        }

        if (Regex.IsMatch(title, @"\b(?:blu[\s._-]?ray|bdrip|brrip|bd25|bd50)\b", RegexOptions.IgnoreCase))
        {
            return ReleaseTier.BluRay;
        }

        if (Regex.IsMatch(title, @"\bweb[\s._-]?rip\b", RegexOptions.IgnoreCase))
        {
            return ReleaseTier.WebRip;
        }

        if (Regex.IsMatch(title, @"\b(?:web[\s._-]?dl|webdl|web)\b", RegexOptions.IgnoreCase))
        {
            return ReleaseTier.WebDl;
        }

        if (Regex.IsMatch(title, @"\b(?:hdtv|pdtv|dsr)\b", RegexOptions.IgnoreCase))
        {
            return ReleaseTier.Hdtv;
        }

        if (Regex.IsMatch(title, @"\b(?:cam|camrip|hdcam|ts|telesync|tc|telecine|hdts)\b", RegexOptions.IgnoreCase))
        {
            return ReleaseTier.Cam;
        }

        return ReleaseTier.Unknown;
    }

    private static string? ParseVideoCodec(string title)
    {
        if (Regex.IsMatch(title, @"\b(?:av1)\b", RegexOptions.IgnoreCase))
        {
            return "AV1";
        }

        if (Regex.IsMatch(title, @"\b(?:x265|h[\s._-]?265|hevc)\b", RegexOptions.IgnoreCase))
        {
            return "H.265";
        }

        if (Regex.IsMatch(title, @"\b(?:x264|h[\s._-]?264|avc)\b", RegexOptions.IgnoreCase))
        {
            return "H.264";
        }

        if (Regex.IsMatch(title, @"\b(?:xvid|divx|mpeg-?2)\b", RegexOptions.IgnoreCase))
        {
            return "Legacy";
        }

        return null;
    }

    private static string? ParseAudio(string title)
    {
        // Letter guards rather than \b for the same reason as the channels below: the channel count
        // often follows the format with no separator ("DDP5.1", "AAC2.0"), and \b needs a non-word
        // character. Bounding on letters instead keeps "dd" from matching inside a word while still
        // allowing a digit to follow.
        var format = Regex.Match(
            title,
            @"(?<![a-z])(truehd|atmos|dts[\s._-]?hd(?:[\s._-]?ma)?|dts[\s._-]?x|dts|e[\s._-]?ac3|eac3|ddp|dd\+|ac3|dd|aac|opus|flac|mp3)(?![a-z])",
            RegexOptions.IgnoreCase);

        // Channel counts butt straight up against the format token ("DDP5.1", "AAC2.0"), so \b is no
        // use — there is no word boundary between a letter and a digit. Guard on digits instead, which
        // also stops the year in "Movie.2024" and the "1" in "1080p" from reading as a channel spec.
        var channels = Regex.Match(title, @"(?<![0-9])(?<main>[0-9])[\s._-](?<sub>[01])(?![0-9])");

        if (!format.Success && !channels.Success)
        {
            return null;
        }

        var label = format.Success ? Normalize(format.Groups[1].Value) : null;
        var ch = channels.Success
            ? $"{channels.Groups["main"].Value}.{channels.Groups["sub"].Value}"
            : null;

        return string.Join(' ', new[] { label, ch }.Where(x => !string.IsNullOrEmpty(x)));
    }

    private static string Normalize(string raw)
    {
        var v = raw.ToLowerInvariant().Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        return v switch
        {
            "truehd" => "TrueHD",
            "atmos" => "Atmos",
            "dtshd" or "dtshdma" => "DTS-HD",
            "dtsx" => "DTS:X",
            "dts" => "DTS",
            "eac3" or "ddp" or "dd+" => "EAC3",
            "ac3" or "dd" => "AC3",
            "aac" => "AAC",
            "opus" => "Opus",
            "flac" => "FLAC",
            "mp3" => "MP3",
            _ => raw.ToUpperInvariant(),
        };
    }

    private static string? ParseGroup(string title)
    {
        var m = GroupPattern.Match(title.Trim());
        return m.Success ? m.Groups["group"].Value : null;
    }

    private static string QualityWord(TorrentSource s) =>
        s.Tier switch
        {
            ReleaseTier.Remux => "Excellent",
            ReleaseTier.BluRay => s.ResolutionHeight >= 1080 ? "Excellent" : "Good",
            ReleaseTier.WebDl => s.ResolutionHeight >= 1080 ? "Great" : "Good",
            ReleaseTier.WebRip => "Good",
            ReleaseTier.Hdtv => "Low",
            ReleaseTier.Cam => "Poor",
            _ => s.ResolutionHeight >= 1080 ? "Good" : "Low",
        };

    // The default line a viewer reads. No tier names, no codecs, no seeder counts — those live
    // behind the advanced expander.
    private static string BuildSummary(TorrentSource s)
    {
        var parts = new List<string>();

        if (s.ResolutionHeight >= 2160)
        {
            parts.Add("4K");
        }
        else if (s.ResolutionHeight > 0)
        {
            parts.Add($"{s.ResolutionHeight}p");
        }

        parts.Add(s.Quality);

        if (s.DolbyVision)
        {
            parts.Add("Dolby Vision");
        }
        else if (s.Hdr)
        {
            parts.Add("HDR");
        }

        if (!string.IsNullOrEmpty(s.Audio))
        {
            parts.Add(s.Audio!);
        }

        if (s.SizeBytes > 0)
        {
            parts.Add(HumanSize(s.SizeBytes));
        }

        return string.Join(" · ", parts);
    }

    private static string HumanSize(long bytes)
    {
        const double Gb = 1024d * 1024 * 1024;
        var gb = bytes / Gb;
        return gb >= 1
            ? $"{gb.ToString(gb >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture)} GB"
            : $"{bytes / (1024d * 1024):0} MB";
    }

    /// <summary>
    /// Collapses re-uploads of the same release. Public indexers list the same thing repeatedly, and
    /// the viewer should see one entry with the healthiest swarm rather than five identical rows.
    /// </summary>
    private static List<TorrentSource> Dedupe(IEnumerable<TorrentSource> sources) =>
        sources
            .GroupBy(s => $"{s.ResolutionHeight}|{(int)s.Tier}|{s.VideoCodec}|{s.ReleaseGroup?.ToLowerInvariant()}|{s.SizeBytes / (256L * 1024 * 1024)}")
            .Select(g => g.OrderByDescending(s => s.Seeders).First())
            .ToList();

    private static double ScoreOf(TorrentSource s, int preferredHeight)
    {
        // Swarm health, saturating at SaturationSeeders. Below that it dominates everything, because
        // 4 seeders versus 40 decides whether playback starts at all. Above it the term is flat: once
        // the swarm can keep the player's buffer full, more peers change nothing, and letting the
        // score keep climbing would rank a well-seeded bad encode above a decent one — which is
        // exactly what it did before this saturated.
        var score = MaxHealthPoints * Math.Min(1.0, Math.Log10(Math.Max(2, s.Seeders)) / Math.Log10(SaturationSeeders));

        // Production tier is the next strongest signal, and the only one that speaks to fidelity
        // independent of resolution.
        score += (int)s.Tier * 6;

        // Resolution is rewarded up to the display and penalised past it — 4K on a 1080p panel
        // spends bandwidth and startup latency on pixels nobody sees.
        if (s.ResolutionHeight > 0)
        {
            score += s.ResolutionHeight <= preferredHeight
                ? 14.0 * s.ResolutionHeight / preferredHeight
                : 14 - 10.0 * (s.ResolutionHeight - preferredHeight) / preferredHeight;
        }

        // Modern codecs deliver the same picture in fewer bits, which is exactly what streaming wants.
        score += s.VideoCodec switch
        {
            "AV1" => 5,
            "H.265" => 4,
            "H.264" => 2,
            "Legacy" => -6,
            _ => 0,
        };

        // A remux is enormous. Wanting one is legitimate, but it should not win the default pick.
        var expected = ExpectedGbFor(s.ResolutionHeight);
        if (s.SizeBytes > 0 && expected > 0)
        {
            var gb = s.SizeBytes / (1024d * 1024 * 1024);
            var ratio = gb / expected;
            if (ratio > 2.5)
            {
                score -= 10;
            }
            else if (ratio < 0.35)
            {
                // Suspiciously small for its claimed resolution — usually a bad upscale or a re-encode
                // that looks far worse than the label promises.
                score -= 12;
            }
        }

        return score;
    }

    // Rough "normal" size for a feature at each resolution, used only to spot outliers in both
    // directions. Not a quality judgement on its own.
    private static double ExpectedGbFor(int height) =>
        height switch
        {
            >= 2160 => 25,
            >= 1440 => 12,
            >= 1080 => 8,
            >= 720 => 4,
            > 0 => 1.5,
            _ => 0,
        };
}
