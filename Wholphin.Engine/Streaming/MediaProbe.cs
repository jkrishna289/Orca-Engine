using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Http;

namespace Wholphin.Engine.Streaming;

/// <summary>
/// Reads the track list out of a torrent's video file with ffprobe, so the client gets real audio and
/// subtitle tracks instead of discovering them itself.
///
/// The file is still downloading, so it cannot simply be handed to ffprobe: most of it is missing. The
/// trick is a sparse scratch file of the right total length, with only the head and tail filled in —
/// which is exactly what MonoTorrent's prebuffer already fetched. ffprobe then seeks around a file
/// that looks complete, finds the Matroska header (head) or the MP4 moov atom (often the tail), and
/// reports the tracks. Any hole it touches reads as zeroes, which it treats as a corrupt region and
/// skips rather than failing outright.
///
/// Entirely fail-soft: no ffprobe, a weird container, or a timeout all yield null, and the caller
/// falls back to letting the player discover tracks on its own.
/// </summary>
public sealed class MediaProbe
{
    // Enough to cover a Matroska header plus generously-sized font attachments, which some anime
    // releases put before the track entries.
    private const int HeadBytes = 12 * 1024 * 1024;

    // Kept inside one typical piece so reading it doesn't block on a fresh download — the prebuffer
    // fetched the final piece, and asking for more than that defeats the point.
    private const int TailBytes = 1 * 1024 * 1024;

    private const int ProbeTimeoutMs = 30_000;

    private readonly ILogger<MediaProbe> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediaProbe"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public MediaProbe(ILogger<MediaProbe> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Probes a partially-downloaded stream. [read] is called to pull bytes at an absolute offset —
    /// the same accessor the HTTP layer uses, so probing goes through piece prioritization too.
    /// </summary>
    /// <param name="totalLength">The video file's full length in bytes.</param>
    /// <param name="read">Reads into a buffer at an absolute offset; returns bytes read.</param>
    /// <param name="scratchDir">Directory for the temporary sparse file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The media info, or null when it couldn't be determined.</returns>
    public async Task<StreamMediaInfo?> ProbeAsync(
        long totalLength,
        Func<long, Memory<byte>, CancellationToken, Task<int>> read,
        string scratchDir,
        CancellationToken cancellationToken)
    {
        if (totalLength <= 0)
        {
            return null;
        }

        var scratch = Path.Combine(scratchDir, $"probe-{Guid.NewGuid():N}.bin");

        try
        {
            Directory.CreateDirectory(scratchDir);
            await BuildScratchFileAsync(scratch, totalLength, read, cancellationToken).ConfigureAwait(false);

            var json = await ExternalProcess.CaptureAsync(
                "ffprobe",
                "-v quiet -print_format json -show_format -show_streams " +
                    // The head may be a partial frame run; let ffprobe work harder before giving up.
                    "-analyzeduration 20000000 -probesize 20000000 " +
                    $"\"{scratch}\"",
                ProbeTimeoutMs,
                _logger,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogDebug("Orca stream: ffprobe returned nothing for a {Bytes}-byte stream", totalLength);
                return null;
            }

            return Parse(json, totalLength);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orca stream: probe failed");
            return null;
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    private static async Task BuildScratchFileAsync(
        string path,
        long totalLength,
        Func<long, Memory<byte>, CancellationToken, Task<int>> read,
        CancellationToken cancellationToken)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        // SetLength on a fresh file gives a sparse region on ext4/NTFS, so this costs no real disk
        // for the gap between head and tail even when the video is many gigabytes.
        fs.SetLength(totalLength);

        var headLength = (int)Math.Min(HeadBytes, totalLength);
        await CopyRegionAsync(fs, 0, headLength, read, cancellationToken).ConfigureAwait(false);

        // Only worth a tail read when there is a real gap; otherwise the head already covers it.
        if (totalLength > (long)headLength + TailBytes)
        {
            var tailStart = totalLength - TailBytes;
            await CopyRegionAsync(fs, tailStart, TailBytes, read, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyRegionAsync(
        FileStream target,
        long offset,
        int length,
        Func<long, Memory<byte>, CancellationToken, Task<int>> read,
        CancellationToken cancellationToken)
    {
        const int ChunkSize = 512 * 1024;
        var buffer = new byte[ChunkSize];
        target.Seek(offset, SeekOrigin.Begin);

        var copied = 0;
        while (copied < length)
        {
            var want = Math.Min(ChunkSize, length - copied);
            var got = await read(offset + copied, buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (got <= 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
            copied += got;
        }
    }

    /// <summary>
    /// Turns ffprobe's JSON into <see cref="StreamMediaInfo"/>. Internal rather than private so the
    /// mapping — track kinds, tag casing, HDR signalling, bitrate derivation — can be tested against
    /// real ffprobe output without needing a torrent.
    /// </summary>
    /// <param name="json">ffprobe's `-print_format json` output.</param>
    /// <param name="totalLength">The video file's true length, for the bitrate derivation.</param>
    /// <returns>Parsed info, or null when no playable track was found.</returns>
    internal static StreamMediaInfo? Parse(string json, long totalLength)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tracks = new List<StreamTrack>();
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var track = ParseTrack(s);
                if (track is not null)
                {
                    tracks.Add(track);
                }
            }
        }

        if (tracks.Count == 0)
        {
            return null;
        }

        long? runTimeTicks = null;
        int? bitrate = null;
        string? container = null;

        if (root.TryGetProperty("format", out var format))
        {
            container = Str(format, "format_name");

            if (double.TryParse(Str(format, "duration"), NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                runTimeTicks = (long)(seconds * TimeSpan.TicksPerSecond);
            }

            if (int.TryParse(Str(format, "bit_rate"), NumberStyles.Any, CultureInfo.InvariantCulture, out var br) && br > 0)
            {
                bitrate = br;
            }
        }

        // A sparse scratch file makes ffprobe's own size-derived bitrate meaningless; recompute it
        // from the real length when a duration is known.
        if (runTimeTicks is > 0)
        {
            var seconds = (double)runTimeTicks.Value / TimeSpan.TicksPerSecond;
            var derived = (int)Math.Min(int.MaxValue, totalLength * 8 / Math.Max(1, seconds));
            bitrate = derived;
        }

        return new StreamMediaInfo
        {
            RunTimeTicks = runTimeTicks,
            Bitrate = bitrate,
            Container = container,
            Streams = tracks,
        };
    }

    private static StreamTrack? ParseTrack(JsonElement s)
    {
        var kind = Str(s, "codec_type")?.ToLowerInvariant();
        var type = kind switch
        {
            "video" => "Video",
            "audio" => "Audio",
            "subtitle" => "Subtitle",
            // Attachments (fonts, cover art) are not playable tracks and must not consume an index
            // in the client's track list.
            _ => null,
        };

        if (type is null)
        {
            return null;
        }

        var disposition = s.TryGetProperty("disposition", out var d) ? d : default;
        var tags = s.TryGetProperty("tags", out var t) ? t : default;

        return new StreamTrack
        {
            Index = s.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : 0,
            Type = type,
            Codec = Str(s, "codec_name"),
            Profile = Str(s, "profile"),
            ColorTransfer = Str(s, "color_transfer"),
            ColorPrimaries = Str(s, "color_primaries"),
            Language = TagValue(tags, "language"),
            Title = TagValue(tags, "title"),
            IsDefault = Flag(disposition, "default"),
            IsForced = Flag(disposition, "forced"),
            Width = Int(s, "width"),
            Height = Int(s, "height"),
            Channels = Int(s, "channels"),
            ChannelLayout = Str(s, "channel_layout"),
            BitRate = int.TryParse(Str(s, "bit_rate"), NumberStyles.Any, CultureInfo.InvariantCulture, out var br) && br > 0
                ? br
                : null,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i)
                ? i
                : null;

    private static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i)
            && i != 0;

    // Container tag keys vary in case between muxers ("language" vs "LANGUAGE"), so match loosely.
    private static string? TagValue(JsonElement tags, string name)
    {
        if (tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in tags.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover scratch file is harmless; the cache folder is swept anyway.
        }
    }
}
