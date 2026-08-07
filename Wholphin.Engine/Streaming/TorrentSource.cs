using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wholphin.Engine.Streaming;

/// <summary>How a release was produced, best first. This is the strongest single quality signal.</summary>
public enum ReleaseTier
{
    /// <summary>Nothing recognizable in the release name.</summary>
    Unknown = 0,

    /// <summary>A camcorder recording or telesync. Effectively unwatchable.</summary>
    Cam = 1,

    /// <summary>Broadcast capture.</summary>
    Hdtv = 2,

    /// <summary>Re-encoded from a streaming source; lossier than WEB-DL.</summary>
    WebRip = 3,

    /// <summary>Direct stream capture, no re-encode.</summary>
    WebDl = 4,

    /// <summary>Re-encoded from disc.</summary>
    BluRay = 5,

    /// <summary>Untouched disc video and audio. Very large.</summary>
    Remux = 6,
}

/// <summary>A candidate stream source, after parsing and scoring.</summary>
public class TorrentSource
{
    /// <summary>Gets or sets the raw release name, shown only under "advanced".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the opaque handle the client passes back to open a stream.
    ///
    /// The client deliberately never receives [DownloadUrl]: Prowlarr embeds the server's API key in
    /// its download links, so returning them would hand every TV client a credential it has no
    /// business holding. The engine resolves this id to the real URL server-side instead.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where the torrent actually comes from — either a <c>magnet:</c> URI or an HTTP
    /// link to a .torrent file. Server-only; excluded from JSON because it can carry an API key.
    /// </summary>
    [JsonIgnore]
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the size in bytes; 0 when the indexer didn't say.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets the seeder count — the dominant predictor of whether streaming works.</summary>
    public int Seeders { get; set; }

    /// <summary>Gets or sets the leecher count.</summary>
    public int Leechers { get; set; }

    /// <summary>Gets or sets the indexer that supplied this result.</summary>
    public string? Indexer { get; set; }

    /// <summary>Gets or sets vertical resolution in pixels (1080, 2160, ...); 0 when unknown.</summary>
    public int ResolutionHeight { get; set; }

    /// <summary>Gets or sets how the release was produced.</summary>
    public ReleaseTier Tier { get; set; }

    /// <summary>Gets or sets the video codec label (e.g. "H.265", "AV1").</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets a value indicating whether the release carries HDR of some form.</summary>
    public bool Hdr { get; set; }

    /// <summary>Gets or sets a value indicating whether the release carries Dolby Vision.</summary>
    public bool DolbyVision { get; set; }

    /// <summary>Gets or sets the audio label (e.g. "TrueHD 7.1", "EAC3 5.1").</summary>
    public string? Audio { get; set; }

    /// <summary>Gets or sets the release group, when the name carried one.</summary>
    public string? ReleaseGroup { get; set; }

    /// <summary>Gets or sets the computed rank score. Higher is better; used for ordering only.</summary>
    public double Score { get; set; }

    /// <summary>
    /// Gets or sets the plain-language quality word shown by default: "Excellent", "Great", "Good",
    /// "Low" or "Poor". This is what a viewer reads instead of the tier and codec.
    /// </summary>
    public string Quality { get; set; } = string.Empty;

    /// <summary>Gets or sets the one-line plain-language summary, e.g. "1080p · Excellent · English 5.1".</summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>Sources bucketed into the handful of choices a viewer is actually offered.</summary>
public class SourceGroups
{
    /// <summary>Gets or sets the single best all-round pick, or null when nothing was usable.</summary>
    public TorrentSource? Recommended { get; set; }

    /// <summary>Gets or sets the highest-fidelity option.</summary>
    public TorrentSource? BestQuality { get; set; }

    /// <summary>Gets or sets the option most likely to start instantly (most seeders).</summary>
    public TorrentSource? FastestStart { get; set; }

    /// <summary>Gets or sets the smallest watchable option, for constrained connections.</summary>
    public TorrentSource? LowestBandwidth { get; set; }

    /// <summary>Gets or sets the best 4K HDR option, when one exists.</summary>
    public TorrentSource? FourKHdr { get; set; }

    /// <summary>Gets or sets every usable source, ranked. Shown only behind "Show all sources".</summary>
    public IReadOnlyList<TorrentSource> All { get; set; } = new List<TorrentSource>();
}
