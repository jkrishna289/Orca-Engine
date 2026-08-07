using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Streaming;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for release-name parsing, scoring and grouping of stream sources.</summary>
public class SourceRankerTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static TorrentSource Src(string title, int seeders = 100, long size = 8 * Gb) =>
        new()
        {
            Title = title,
            DownloadUrl = "magnet:?xt=urn:btih:" + title.GetHashCode().ToString("x8"),
            Seeders = seeders,
            SizeBytes = size,
        };

    private static TorrentSource Described(string title) => SourceRanker.Describe(Src(title));

    // ── Release-name parsing ────────────────────────────────────────────────

    [Theory]
    [InlineData("Movie.2024.2160p.WEB-DL.x265-GRP", 2160)]
    [InlineData("Movie.2024.1080p.BluRay.x264-GRP", 1080)]
    [InlineData("Movie.2024.720p.HDTV.x264-GRP", 720)]
    [InlineData("Movie 2024 4K WEB-DL", 2160)]
    [InlineData("Movie.2024.UHD.BluRay.REMUX", 2160)]
    [InlineData("Movie.2024.576i.DVD", 576)]
    public void ParsesResolution(string title, int expected) =>
        Assert.Equal(expected, Described(title).ResolutionHeight);

    [Theory]
    [InlineData("Movie.2024.1080p.BluRay.REMUX.AVC-GRP", ReleaseTier.Remux)]
    [InlineData("Movie.2024.1080p.BluRay.x264-GRP", ReleaseTier.BluRay)]
    [InlineData("Movie.2024.1080p.WEB-DL.DDP5.1-GRP", ReleaseTier.WebDl)]
    [InlineData("Movie.2024.1080p.WEBRip.x264-GRP", ReleaseTier.WebRip)]
    [InlineData("Movie.2024.720p.HDTV.x264-GRP", ReleaseTier.Hdtv)]
    [InlineData("Movie.2024.HDCAM.x264-GRP", ReleaseTier.Cam)]
    public void ParsesReleaseTier(string title, ReleaseTier expected) =>
        Assert.Equal(expected, Described(title).Tier);

    [Fact]
    public void RemuxWins_OverTheBluRayTagInTheSameName()
    {
        // "BluRay REMUX" contains both markers; the stronger one has to win.
        Assert.Equal(ReleaseTier.Remux, Described("Movie.2024.1080p.BluRay.REMUX.AVC.TrueHD-GRP").Tier);
    }

    [Fact]
    public void WebRipIsNotMistakenForWebDl()
    {
        Assert.Equal(ReleaseTier.WebRip, Described("Movie.2024.1080p.WEBRip.x264-GRP").Tier);
        Assert.Equal(ReleaseTier.WebDl, Described("Movie.2024.1080p.WEB-DL.x264-GRP").Tier);
    }

    [Theory]
    [InlineData("Movie.2024.1080p.WEB-DL.x265-GRP", "H.265")]
    [InlineData("Movie.2024.1080p.WEB-DL.HEVC-GRP", "H.265")]
    [InlineData("Movie.2024.1080p.WEB-DL.x264-GRP", "H.264")]
    [InlineData("Movie.2024.1080p.WEB-DL.AV1-GRP", "AV1")]
    [InlineData("Movie.2024.XviD-GRP", "Legacy")]
    public void ParsesVideoCodec(string title, string expected) =>
        Assert.Equal(expected, Described(title).VideoCodec);

    [Fact]
    public void DetectsHdrAndDolbyVision()
    {
        Assert.True(Described("Movie.2024.2160p.WEB-DL.HDR10.x265-GRP").Hdr);
        Assert.True(Described("Movie.2024.2160p.WEB-DL.HDR10+.x265-GRP").Hdr);

        var dv = Described("Movie.2024.2160p.WEB-DL.DV.HDR.x265-GRP");
        Assert.True(dv.DolbyVision);
        // Dolby Vision implies HDR — a viewer should never see "DV but not HDR".
        Assert.True(dv.Hdr);

        Assert.False(Described("Movie.2024.1080p.BluRay.x264-GRP").Hdr);
    }

    [Theory]
    [InlineData("Movie.2024.1080p.BluRay.TrueHD.7.1.x264-GRP", "TrueHD 7.1")]
    [InlineData("Movie.2024.1080p.WEB-DL.DDP5.1.x264-GRP", "EAC3 5.1")]
    [InlineData("Movie.2024.1080p.WEB-DL.AAC2.0.x264-GRP", "AAC 2.0")]
    [InlineData("Movie.2024.1080p.BluRay.DTS-HD.MA.5.1-GRP", "DTS-HD 5.1")]
    public void ParsesAudio(string title, string expected) =>
        Assert.Equal(expected, Described(title).Audio);

    [Fact]
    public void ParsesReleaseGroup()
    {
        Assert.Equal("SPARKS", Described("Movie.2024.1080p.BluRay.x264-SPARKS").ReleaseGroup);
        Assert.Null(Described("Movie 2024 1080p BluRay").ReleaseGroup);
    }

    // ── Viewer-facing wording ───────────────────────────────────────────────

    [Fact]
    public void SummaryUsesPlainLanguage_AndNoJargon()
    {
        var s = Described("Movie.2024.2160p.BluRay.REMUX.HDR10.TrueHD.7.1-GRP");
        s.SizeBytes = 40 * Gb;
        SourceRanker.Describe(s);

        Assert.Equal("4K · Excellent · HDR · TrueHD 7.1 · 40 GB", s.Summary);

        // The words a viewer must never be shown by default.
        foreach (var jargon in new[] { "REMUX", "seed", "magnet", "torrent", "x265", "BluRay" })
        {
            Assert.DoesNotContain(jargon, s.Summary, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void QualityWordReflectsTierAndResolution()
    {
        Assert.Equal("Excellent", Described("M.2024.1080p.BluRay.REMUX-G").Quality);
        Assert.Equal("Great", Described("M.2024.1080p.WEB-DL-G").Quality);
        Assert.Equal("Low", Described("M.2024.720p.HDTV-G").Quality);
    }

    // ── Filtering, dedup and grouping ───────────────────────────────────────

    [Fact]
    public void DropsCamReleases_EvenWhenWellSeeded()
    {
        var groups = SourceRanker.Rank(new[]
        {
            Src("Movie.2024.HDCAM.x264-GRP", seeders: 5000),
            Src("Movie.2024.1080p.WEB-DL.x264-GRP", seeders: 50),
        });

        Assert.Single(groups.All);
        Assert.DoesNotContain(groups.All, s => s.Tier == ReleaseTier.Cam);
    }

    [Fact]
    public void DropsDeadTorrents()
    {
        var groups = SourceRanker.Rank(new[]
        {
            Src("Movie.2024.1080p.BluRay.REMUX-GRP", seeders: 0),
            Src("Movie.2024.1080p.WEB-DL.x264-GRP", seeders: 40),
        });

        Assert.Single(groups.All);
        Assert.Equal(40, groups.All[0].Seeders);
    }

    [Fact]
    public void DropsSourcesWithoutAMagnet()
    {
        var noLink = new TorrentSource { Title = "Movie.2024.1080p.WEB-DL-GRP", Seeders = 500, DownloadUrl = "" };
        Assert.Empty(SourceRanker.Rank(new[] { noLink }).All);
    }

    [Fact]
    public void DedupesReUploadsOfTheSameRelease_KeepingTheHealthiestSwarm()
    {
        var groups = SourceRanker.Rank(new[]
        {
            Src("Movie.2024.1080p.WEB-DL.x264-GRP", seeders: 12, size: 8 * Gb),
            Src("Movie.2024.1080p.WEB-DL.x264-GRP", seeders: 340, size: 8 * Gb),
            Src("Movie.2024.1080p.WEB-DL.x264-GRP", seeders: 90, size: 8 * Gb),
        });

        Assert.Single(groups.All);
        Assert.Equal(340, groups.All[0].Seeders);
    }

    [Fact]
    public void RecommendedPrefersHealthySwarm_OverAHugeRemuxWithFewPeers()
    {
        var groups = SourceRanker.Rank(new[]
        {
            Src("Movie.2024.2160p.BluRay.REMUX.HDR10.TrueHD.7.1-GRP", seeders: 3, size: 60 * Gb),
            Src("Movie.2024.1080p.WEB-DL.DDP5.1.x265-GRP", seeders: 900, size: 6 * Gb),
        });

        // This is the whole bias of the ranker: a stream that starts beats a stream that is prettier
        // on paper but stalls.
        Assert.Equal(ReleaseTier.WebDl, groups.Recommended!.Tier);
        Assert.Equal(900, groups.Recommended.Seeders);

        // "Best quality" still surfaces the remux for anyone who explicitly wants it.
        Assert.Equal(2160, groups.BestQuality!.ResolutionHeight);
    }

    [Fact]
    public void PrefersDisplayResolution_OverHigher()
    {
        var groups = SourceRanker.Rank(
            new[]
            {
                Src("Movie.2024.2160p.WEB-DL.x265-GRP", seeders: 200, size: 25 * Gb),
                Src("Movie.2024.1080p.WEB-DL.x265-GRP", seeders: 200, size: 8 * Gb),
            },
            preferredHeight: 1080);

        // On a 1080p panel the 4K bitrate buys nothing and costs startup time.
        Assert.Equal(1080, groups.Recommended!.ResolutionHeight);
    }

    [Fact]
    public void PrefersHigherResolution_OnA4kDisplay()
    {
        var groups = SourceRanker.Rank(
            new[]
            {
                Src("Movie.2024.2160p.WEB-DL.x265-GRP", seeders: 200, size: 25 * Gb),
                Src("Movie.2024.1080p.WEB-DL.x265-GRP", seeders: 200, size: 8 * Gb),
            },
            preferredHeight: 2160);

        Assert.Equal(2160, groups.Recommended!.ResolutionHeight);
    }

    [Fact]
    public void PenalisesImplausiblySmallReleasesForTheirClaimedResolution()
    {
        var groups = SourceRanker.Rank(new[]
        {
            // 900MB claiming 1080p: a bad re-encode that looks far worse than the label.
            Src("Movie.2024.1080p.WEB-DL.x264-TINY", seeders: 400, size: 900L * 1024 * 1024),
            Src("Movie.2024.1080p.WEB-DL.x264-NORMAL", seeders: 120, size: 8 * Gb),
        });

        Assert.Equal("NORMAL", groups.Recommended!.ReleaseGroup);
    }

    [Fact]
    public void GroupsExposeDistinctChoices()
    {
        var groups = SourceRanker.Rank(new[]
        {
            Src("Movie.2024.2160p.BluRay.REMUX.HDR10.TrueHD.7.1-BIG", seeders: 40, size: 55 * Gb),
            Src("Movie.2024.1080p.WEB-DL.DDP5.1.x265-MID", seeders: 900, size: 6 * Gb),
            Src("Movie.2024.720p.WEB-DL.AAC2.0.x264-SMALL", seeders: 200, size: 1500L * 1024 * 1024),
        });

        Assert.Equal(2160, groups.BestQuality!.ResolutionHeight);
        Assert.Equal(900, groups.FastestStart!.Seeders);
        Assert.Equal("SMALL", groups.LowestBandwidth!.ReleaseGroup);
        Assert.Equal(2160, groups.FourKHdr!.ResolutionHeight);
        Assert.Equal(3, groups.All.Count);
    }

    [Fact]
    public void FourKHdrIsNull_WhenNoSuchSourceExists()
    {
        var groups = SourceRanker.Rank(new[] { Src("Movie.2024.1080p.WEB-DL.x264-GRP", seeders: 300) });
        Assert.Null(groups.FourKHdr);
    }

    [Fact]
    public void EmptyInputYieldsEmptyGroups()
    {
        var groups = SourceRanker.Rank(new List<TorrentSource>());

        Assert.Empty(groups.All);
        Assert.Null(groups.Recommended);
        Assert.Null(groups.BestQuality);
    }

    [Fact]
    public void CapsResultCount_SoTheListStaysNavigableOnARemote()
    {
        var many = Enumerable.Range(0, 80)
            .Select(i => Src($"Movie.2024.1080p.WEB-DL.x264-GRP{i}", seeders: 10 + i, size: (6 + (i % 5)) * Gb))
            .ToList();

        Assert.True(SourceRanker.Rank(many).All.Count <= 20);
    }
}
