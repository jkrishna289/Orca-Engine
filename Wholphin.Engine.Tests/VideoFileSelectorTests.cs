using System.Collections.Generic;
using Wholphin.Engine.Streaming;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for torrent video-file selection.</summary>
public class VideoFileSelectorTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static TorrentFileEntry F(string path, long length) => new(path, length);

    [Fact]
    public void PicksLargestVideo_IgnoringNonVideoFiles()
    {
        var files = new List<TorrentFileEntry>
        {
            F("Dune.Part.Two.2024.1080p.mkv", 6 * Gb),
            F("Dune.Part.Two.2024.1080p.nfo", 2048),
            F("poster.jpg", 500_000),
            F("Subs/English.srt", 80_000),
        };

        Assert.Equal("Dune.Part.Two.2024.1080p.mkv", VideoFileSelector.Select(files)!.Path);
    }

    [Fact]
    public void RejectsSampleByName_EvenWhenItIsAVideo()
    {
        var files = new List<TorrentFileEntry>
        {
            F("Sample/movie-sample.mkv", 5 * Gb),
            F("movie.mkv", 4 * Gb),
        };

        // The sample is deliberately the larger file here: the name rule must win over size.
        Assert.Equal("movie.mkv", VideoFileSelector.Select(files)!.Path);
    }

    [Fact]
    public void RejectsTrailersAndFeaturettes()
    {
        var files = new List<TorrentFileEntry>
        {
            F("movie.mkv", 6 * Gb),
            F("Extras/behind the scenes.mkv", 2 * Gb),
            F("trailer.mp4", 300 * 1024 * 1024),
            F("Featurette-making-of.mkv", 1 * Gb),
        };

        Assert.Equal("movie.mkv", VideoFileSelector.Select(files)!.Path);
    }

    [Fact]
    public void RejectsUndersizedFiles_ThatDodgeTheNameFilter()
    {
        var files = new List<TorrentFileEntry>
        {
            F("movie.mkv", 8 * Gb),
            // 1% of the feature, innocuously named — only the size floor catches this.
            F("intro.mkv", 80 * 1024 * 1024),
        };

        Assert.Equal("movie.mkv", VideoFileSelector.Select(files)!.Path);
    }

    [Fact]
    public void PicksRequestedEpisode_FromASeasonPack()
    {
        var files = new List<TorrentFileEntry>
        {
            F("Show.S01E01.1080p.mkv", 3 * Gb),
            F("Show.S01E02.1080p.mkv", 3 * Gb),
            F("Show.S01E03.1080p.mkv", 4 * Gb),
        };

        var picked = VideoFileSelector.Select(files, season: 1, episode: 2);
        Assert.Equal("Show.S01E02.1080p.mkv", picked!.Path);
    }

    [Fact]
    public void EpisodeMatching_ToleratesSeparatorVariants()
    {
        var files = new List<TorrentFileEntry>
        {
            F("Show s1.e1 720p.mkv", 2 * Gb),
            F("Show s1.e2 720p.mkv", 2 * Gb),
        };

        Assert.Equal("Show s1.e2 720p.mkv", VideoFileSelector.Select(files, 1, 2)!.Path);
    }

    [Fact]
    public void FallsBackToLargest_WhenRequestedEpisodeIsAbsent()
    {
        var files = new List<TorrentFileEntry>
        {
            F("Show.S01E01.mkv", 3 * Gb),
            F("Show.S01E02.mkv", 5 * Gb),
        };

        // Asking for an episode the pack doesn't contain should still yield something playable.
        Assert.Equal("Show.S01E02.mkv", VideoFileSelector.Select(files, season: 1, episode: 9)!.Path);
    }

    [Fact]
    public void FallsBackToJunkNamed_WhenEveryVideoLooksLikeAnExtra()
    {
        // "Extras" here is part of the actual title — returning null would be the worse answer.
        var files = new List<TorrentFileEntry> { F("The.Extras.2019.1080p.mkv", 5 * Gb) };

        Assert.Equal("The.Extras.2019.1080p.mkv", VideoFileSelector.Select(files)!.Path);
    }

    [Fact]
    public void ReturnsNull_WhenTorrentHasNoVideo()
    {
        var files = new List<TorrentFileEntry>
        {
            F("readme.txt", 1024),
            F("cover.jpg", 200_000),
        };

        Assert.Null(VideoFileSelector.Select(files));
    }

    [Fact]
    public void ReturnsNull_ForEmptyInput()
    {
        Assert.Null(VideoFileSelector.Select(new List<TorrentFileEntry>()));
    }
}
