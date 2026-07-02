using System.Text.Json.Serialization;

namespace Wholphin.Engine.Contracts;

/// <summary>Which source authoritatively identifies a media reference.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MediaSource
{
    /// <summary>Identified by a Jellyfin item id (available in the library).</summary>
    Jellyfin,

    /// <summary>Identified by a TMDB id (discoverable/requestable).</summary>
    Tmdb
}

/// <summary>
/// The dynamic card render type. v1 values map to existing app composables;
/// values >= 100 are reserved and fall back to <see cref="PosterPortrait"/> until the app implements them.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardType
{
    /// <summary>Portrait poster (→ GridCard).</summary>
    PosterPortrait = 0,

    /// <summary>Landscape/wide (→ BannerCard).</summary>
    BannerWide = 1,

    /// <summary>Episode tile (→ EpisodeCard).</summary>
    Episode = 2,

    /// <summary>Circular person avatar (→ PersonCard).</summary>
    PersonCircle = 3,

    /// <summary>Genre tile (→ GenreCard).</summary>
    Genre = 4,

    /// <summary>Studio tile (→ StudioCard).</summary>
    Studio = 5,

    /// <summary>Requestable/discovery item (→ DiscoverItemCard).</summary>
    Discover = 6,

    /// <summary>Generic season/box-set (→ SeasonCard).</summary>
    Season = 7,

    /// <summary>Reserved: large spotlight card.</summary>
    Hero = 100,

    /// <summary>Reserved: numbered top-10 card.</summary>
    TopRanked = 101,

    /// <summary>Reserved: logo-forward card.</summary>
    Logo = 102,

    /// <summary>Reserved: now-playing card.</summary>
    NowPlaying = 103
}

/// <summary>Which image the card should load.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardImageType
{
    /// <summary>Primary/poster image.</summary>
    Primary,

    /// <summary>Backdrop image.</summary>
    Backdrop,

    /// <summary>Thumbnail/still.</summary>
    Thumb,

    /// <summary>Logo image.</summary>
    Logo
}

/// <summary>Card aspect ratio hint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardAspectRatio
{
    /// <summary>2:3 portrait.</summary>
    Tall,

    /// <summary>16:9 landscape.</summary>
    Wide,

    /// <summary>1:1 square.</summary>
    Square,

    /// <summary>4:3.</summary>
    FourThree
}

/// <summary>An action the card can offer, driven by availability.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardAction
{
    /// <summary>Play from the beginning.</summary>
    Play,

    /// <summary>Resume from a saved position.</summary>
    Resume,

    /// <summary>Request via Jellyseerr.</summary>
    Request,

    /// <summary>Open the details page.</summary>
    Details
}

/// <summary>Relative card size, for visual variety across rows (the app maps this to a height scale).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardSize
{
    /// <summary>Default size.</summary>
    Standard,

    /// <summary>Larger (e.g. a featured "For You" row).</summary>
    Large
}

/// <summary>How a row of cards is laid out.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RowStyle
{
    /// <summary>Standard horizontal carousel.</summary>
    Standard,

    /// <summary>Large spotlight row.</summary>
    Hero,

    /// <summary>Numbered top-10 row.</summary>
    Top10,

    /// <summary>Circular (person) row.</summary>
    Circle
}
