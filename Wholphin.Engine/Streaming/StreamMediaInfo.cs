using System.Collections.Generic;

namespace Wholphin.Engine.Streaming;

/// <summary>
/// Container-level facts about a torrent's video file, as reported by ffprobe.
///
/// Deliberately the engine's own shape rather than Jellyfin's <c>MediaStream</c>: the consumer is the
/// Android client, which maps this onto the Jellyfin SDK's Kotlin types itself. Returning Jellyfin's
/// server-side model here would couple the wire format to a type the client never sees.
/// </summary>
public class StreamMediaInfo
{
    /// <summary>Gets or sets the duration in Jellyfin ticks (100ns), or null when unknown.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the overall bitrate in bits per second, or null when unknown.</summary>
    public int? Bitrate { get; set; }

    /// <summary>Gets or sets the container short name (e.g. "matroska,webm", "mov,mp4").</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the tracks found in the container, in container order.</summary>
    public IReadOnlyList<StreamTrack> Streams { get; set; } = new List<StreamTrack>();
}

/// <summary>One track inside the container.</summary>
public class StreamTrack
{
    /// <summary>
    /// Gets or sets the container's own stream index. Passed through unchanged: the client's track
    /// selection maps these to player track ids, so renumbering them would break subtitle picking.
    /// </summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the track kind: "Video", "Audio" or "Subtitle".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the codec short name (e.g. "h264", "eac3", "subrip").</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets the ISO 639-2 language tag when the release tagged one.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the track title, e.g. "Commentary" or "Signs &amp; Songs".</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether the container marks this track default.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether the container marks this track forced.</summary>
    public bool IsForced { get; set; }

    /// <summary>Gets or sets the video width in pixels.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the video height in pixels.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the audio channel count.</summary>
    public int? Channels { get; set; }

    /// <summary>Gets or sets the audio channel layout (e.g. "5.1").</summary>
    public string? ChannelLayout { get; set; }

    /// <summary>Gets or sets the track bitrate in bits per second.</summary>
    public int? BitRate { get; set; }

    /// <summary>Gets or sets the codec profile (e.g. "High", "Main 10") — used for HDR hints.</summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Gets or sets the transfer characteristic (e.g. "smpte2084" for HDR10, "arib-std-b67" for HLG).
    /// This is what actually identifies HDR — "Main 10" only means 10-bit, which SDR content also uses,
    /// so guessing from the profile would misreport plenty of SDR releases as HDR.
    /// </summary>
    public string? ColorTransfer { get; set; }

    /// <summary>Gets or sets the colour primaries (e.g. "bt2020"), a secondary HDR signal.</summary>
    public string? ColorPrimaries { get; set; }
}
