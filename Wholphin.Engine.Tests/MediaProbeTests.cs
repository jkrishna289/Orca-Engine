using System;
using System.Linq;
using Wholphin.Engine.Streaming;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the ffprobe JSON mapping behind torrent stream track metadata.</summary>
public class MediaProbeTests
{
    private const long TwoGb = 2L * 1024 * 1024 * 1024;

    // Shaped like real ffprobe output for a typical 1080p release: h264 video, two audio tracks in
    // different languages, one text subtitle, plus a font attachment that must not become a track.
    private const string TypicalMkv = """
    {
      "streams": [
        { "index": 0, "codec_type": "video", "codec_name": "h264", "profile": "High",
          "width": 1920, "height": 1080, "color_transfer": "bt709",
          "disposition": { "default": 1, "forced": 0 } },
        { "index": 1, "codec_type": "audio", "codec_name": "eac3", "channels": 6,
          "channel_layout": "5.1", "bit_rate": "640000",
          "disposition": { "default": 1, "forced": 0 },
          "tags": { "language": "eng", "title": "Surround 5.1" } },
        { "index": 2, "codec_type": "audio", "codec_name": "aac", "channels": 2,
          "channel_layout": "stereo",
          "disposition": { "default": 0, "forced": 0 },
          "tags": { "LANGUAGE": "jpn" } },
        { "index": 3, "codec_type": "subtitle", "codec_name": "subrip",
          "disposition": { "default": 0, "forced": 1 },
          "tags": { "language": "eng" } },
        { "index": 4, "codec_type": "attachment", "codec_name": "ttf",
          "tags": { "filename": "Roboto.ttf" } }
      ],
      "format": { "format_name": "matroska,webm", "duration": "7200.000000", "bit_rate": "1200" }
    }
    """;

    [Fact]
    public void MapsVideoAudioAndSubtitleTracks()
    {
        var info = MediaProbe.Parse(TypicalMkv, TwoGb);

        Assert.NotNull(info);
        Assert.Equal("Video", info!.Streams[0].Type);
        Assert.Equal("Audio", info.Streams[1].Type);
        Assert.Equal("Audio", info.Streams[2].Type);
        Assert.Equal("Subtitle", info.Streams[3].Type);
    }

    [Fact]
    public void ExcludesAttachments_SoTheyDoNotConsumeATrackSlot()
    {
        var info = MediaProbe.Parse(TypicalMkv, TwoGb);

        // The font attachment is stream index 4 in the container but must not appear as a track.
        Assert.Equal(4, info!.Streams.Count);
        Assert.DoesNotContain(info.Streams, s => s.Codec == "ttf");
    }

    [Fact]
    public void PreservesContainerStreamIndices()
    {
        var info = MediaProbe.Parse(TypicalMkv, TwoGb);

        // Renumbering these would shift the client's subtitle/audio selection.
        Assert.Equal(new[] { 0, 1, 2, 3 }, info!.Streams.Select(s => s.Index).ToArray());
    }

    [Fact]
    public void ReadsLanguageTags_RegardlessOfTagCasing()
    {
        var info = MediaProbe.Parse(TypicalMkv, TwoGb);

        Assert.Equal("eng", info!.Streams[1].Language);
        // Written as "LANGUAGE" by some muxers.
        Assert.Equal("jpn", info.Streams[2].Language);
    }

    [Fact]
    public void ReadsDispositionFlagsAndAudioLayout()
    {
        var info = MediaProbe.Parse(TypicalMkv, TwoGb);

        Assert.True(info!.Streams[1].IsDefault);
        Assert.False(info.Streams[2].IsDefault);
        Assert.True(info.Streams[3].IsForced);
        Assert.Equal(6, info.Streams[1].Channels);
        Assert.Equal("5.1", info.Streams[1].ChannelLayout);
        Assert.Equal("Surround 5.1", info.Streams[1].Title);
    }

    [Fact]
    public void ConvertsDurationToTicks()
    {
        var info = MediaProbe.Parse(TypicalMkv, TwoGb);

        Assert.Equal(TimeSpan.FromHours(2).Ticks, info!.RunTimeTicks);
    }

    [Fact]
    public void DerivesBitrateFromRealLength_NotFfprobesSparseFileGuess()
    {
        var info = MediaProbe.Parse(TypicalMkv, TwoGb);

        // ffprobe saw a sparse scratch file and reported 1200 bps, which is meaningless. The real
        // figure comes from the true byte length over the duration: 2GB * 8 / 7200s.
        var expected = (int)(TwoGb * 8 / 7200);
        Assert.Equal(expected, info!.Bitrate);
    }

    [Fact]
    public void DetectsHdr10FromTransferFunction()
    {
        var json = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "hevc", "profile": "Main 10",
              "color_transfer": "smpte2084", "color_primaries": "bt2020" }
          ],
          "format": { "format_name": "matroska,webm", "duration": "5400.0" }
        }
        """;

        var info = MediaProbe.Parse(json, TwoGb);

        Assert.Equal("smpte2084", info!.Streams[0].ColorTransfer);
        Assert.Equal("bt2020", info.Streams[0].ColorPrimaries);
    }

    [Fact]
    public void ReturnsNull_WhenNoPlayableTrackExists()
    {
        // ffprobe succeeded but found only an attachment — nothing to play.
        var json = """
        { "streams": [ { "index": 0, "codec_type": "attachment", "codec_name": "ttf" } ],
          "format": { "format_name": "matroska,webm" } }
        """;

        Assert.Null(MediaProbe.Parse(json, TwoGb));
    }

    [Fact]
    public void ToleratesMissingFormatAndTagBlocks()
    {
        // A partial file often yields no duration and no tags; the tracks must still come through.
        var json = """
        { "streams": [ { "index": 0, "codec_type": "video", "codec_name": "av1" } ] }
        """;

        var info = MediaProbe.Parse(json, TwoGb);

        Assert.NotNull(info);
        Assert.Equal("av1", info!.Streams[0].Codec);
        Assert.Null(info.RunTimeTicks);
        Assert.Null(info.Streams[0].Language);
    }
}
