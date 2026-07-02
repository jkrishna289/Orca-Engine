using System;
using System.Collections.Generic;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Contracts;

/// <summary>
/// A source-agnostic media reference. The app plays via <see cref="JellyfinId"/> when present,
/// otherwise requests via <see cref="TmdbId"/>.
/// </summary>
public class MediaIdDto
{
    /// <summary>Gets or sets the authoritative source.</summary>
    public MediaSource Source { get; set; }

    /// <summary>Gets or sets the Jellyfin id (present when available in the library).</summary>
    public Guid? JellyfinId { get; set; }

    /// <summary>Gets or sets the TMDB id (present when known).</summary>
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the media type.</summary>
    public MediaType MediaType { get; set; }

    /// <summary>Gets or sets the availability state.</summary>
    public AvailabilityState Availability { get; set; }
}

/// <summary>A small overlay badge (e.g., 4K, HDR, NEW, REQUEST, rank, STUDIO).</summary>
public class CardBadge
{
    /// <summary>Gets or sets the badge kind (e.g., AVAILABILITY, NEW, RANK, QUALITY, STUDIO).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional badge text.</summary>
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets an optional icon URL for the badge (e.g., a cached studio/provider logo). Server-relative
    /// (e.g., <c>/OrcaEngine/Images/Provider/8</c>) — the client resolves it against the server base URL.
    /// Null → the client renders <see cref="Text"/> as a pill instead.
    /// </summary>
    public string? IconUrl { get; set; }
}

/// <summary>A dynamic, per-item render instruction.</summary>
public class CardDescriptor
{
    /// <summary>Gets or sets the card type.</summary>
    public CardType Type { get; set; }

    /// <summary>Gets or sets which image to load.</summary>
    public CardImageType ImageType { get; set; } = CardImageType.Primary;

    /// <summary>Gets or sets the aspect-ratio hint.</summary>
    public CardAspectRatio AspectRatio { get; set; } = CardAspectRatio.Tall;

    /// <summary>Gets or sets the relative card size (visual variety across rows).</summary>
    public CardSize Size { get; set; } = CardSize.Standard;

    /// <summary>Gets or sets an absolute image URL the client can load directly (poster/thumb/logo).</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Gets or sets an absolute backdrop (16:9) URL for billboard/hero cards. Null → client resolves from the media id.</summary>
    public string? BackdropImageUrl { get; set; }

    /// <summary>Gets or sets an absolute logo URL for billboard/hero cards. Null → client resolves from the media id.</summary>
    public string? LogoImageUrl { get; set; }

    /// <summary>Gets or sets a directly-playable trailer stream URL for the billboard. Null → client resolves a local trailer.</summary>
    public string? TrailerStreamUrl { get; set; }

    /// <summary>Gets or sets the offset (ms) to skip into the trailer (past studio logos). Hero/billboard only.</summary>
    public int TrailerStartOffsetMs { get; set; }

    /// <summary>Gets or sets how long (ms) the card must be focused before a trailer auto-plays. Hero/billboard only.</summary>
    public int AutoPlayDelayMs { get; set; }

    /// <summary>Gets or sets a value indicating whether this card wants an auto-playing trailer (if the client can render inline video).</summary>
    public bool WantsTrailer { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the subtitle.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets a short synopsis for the instant-details overlay (no extra network needed).</summary>
    public string? Synopsis { get; set; }

    /// <summary>Gets or sets a value indicating whether to show the title.</summary>
    public bool ShowTitle { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to show a progress bar.</summary>
    public bool ShowProgress { get; set; }

    /// <summary>Gets or sets a value indicating whether to show the watched indicator.</summary>
    public bool ShowWatched { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to show the favorite indicator.</summary>
    public bool ShowFavorite { get; set; } = true;

    /// <summary>Gets or sets the resume progress (0..1), if any.</summary>
    public double? Progress { get; set; }

    /// <summary>Gets or sets the overlay badges.</summary>
    public List<CardBadge> Badges { get; set; } = new();

    /// <summary>Gets or sets the available actions.</summary>
    public List<CardAction> Actions { get; set; } = new();

    /// <summary>Gets or sets an optional accent-color hint.</summary>
    public string? AccentColorHint { get; set; }
}

/// <summary>A renderable item: a media reference plus how to render it.</summary>
public class RenderItem
{
    /// <summary>Gets or sets the media reference.</summary>
    public MediaIdDto Media { get; set; } = new();

    /// <summary>Gets or sets the card descriptor.</summary>
    public CardDescriptor Card { get; set; } = new();
}

/// <summary>A row of renderable items.</summary>
public class RenderRow
{
    /// <summary>Gets or sets the stable row id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the row title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the row layout style.</summary>
    public RowStyle RowStyle { get; set; } = RowStyle.Standard;

    /// <summary>Gets or sets the items in the row.</summary>
    public List<RenderItem> Items { get; set; } = new();
}

/// <summary>The full server-driven render payload returned to the app.</summary>
public class RenderBundle
{
    /// <summary>Gets or sets the card contract version.</summary>
    public int ContractVersion { get; set; } = 1;

    /// <summary>Gets or sets the rows.</summary>
    public List<RenderRow> Rows { get; set; } = new();
}

/// <summary>What the client can render — sent so the engine emits only supported cards.</summary>
public class ClientCapabilities
{
    /// <summary>Gets or sets the card contract version the client implements.</summary>
    public int CardContractVersion { get; set; } = 1;

    /// <summary>Gets or sets the card types the client can render.</summary>
    public List<CardType> SupportedCardTypes { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the client can render inline auto-playing video (billboard trailers).</summary>
    public bool SupportsInlineVideo { get; set; }
}
