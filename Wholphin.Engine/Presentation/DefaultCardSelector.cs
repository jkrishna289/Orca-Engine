using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Presentation;

/// <summary>
/// Tier-1 rule/context-based card selector (the "dynamic" decision), with capability-gated
/// fallback so the engine never emits a card the client can't render. Learned per-user
/// card preference is a later phase.
/// </summary>
public class DefaultCardSelector : ICardSelector
{
    private const int NewItemDays = 14;
    private const int TaglineMaxLength = 180;
    private const int SynopsisMaxLength = 260;
    private const int DefaultAutoPlayDelayMs = 2000;
    private const int DefaultTrailerStartOffsetMs = 3000;

    private static readonly HashSet<CardType> BaseV1Types = new()
    {
        CardType.PosterPortrait, CardType.BannerWide, CardType.Episode,
        CardType.PersonCircle, CardType.Genre, CardType.Studio,
        CardType.Discover, CardType.Season,
    };

    /// <inheritdoc />
    public CardDescriptor Select(CatalogItem item, CardSelectionContext context)
    {
        var requestable = item.Availability != AvailabilityState.WatchNow
                          && item.Availability != AvailabilityState.RecentlyAdded;

        var type = ChooseType(item, context, requestable);
        type = ApplyCapabilities(type, requestable, context.Capabilities);

        var descriptor = new CardDescriptor
        {
            Type = type,
            ImageType = ImageFor(type),
            AspectRatio = AspectFor(type),
            Title = item.Title,
            ShowProgress = context.HasResume,
            Actions = ActionsFor(item, context),
            Badges = BadgesFor(item, context),
        };

        if (type is CardType.PosterPortrait or CardType.TopRanked)
        {
            // Poster art already bakes in the title (matches the app's native showTitles=false convention).
            descriptor.ShowTitle = false;
        }

        // Short synopsis carried on every card so the app's instant-details overlay needs no network.
        descriptor.Synopsis = ShortSynopsis(item.Overview);

        // Featured "For You" row gets larger cards for visual variety across the home.
        if (string.Equals(context.RowPurpose, "recommended", StringComparison.OrdinalIgnoreCase))
        {
            descriptor.Size = CardSize.Large;
        }

        if (type == CardType.Hero)
        {
            // Billboard: tagline from the overview, and a trailer hint when the client can play inline video.
            descriptor.Subtitle = Tagline(item.Overview);
            descriptor.WantsTrailer = context.Capabilities.SupportsInlineVideo
                                      && item.Availability is AvailabilityState.WatchNow or AvailabilityState.RecentlyAdded;
            descriptor.AutoPlayDelayMs = DefaultAutoPlayDelayMs;
            descriptor.TrailerStartOffsetMs = DefaultTrailerStartOffsetMs;

            // Metadata chips: year/rating/genre, built from data already on the catalog item.
            if (item.ProductionYear is { } year)
            {
                descriptor.Badges.Add(new CardBadge { Kind = "YEAR", Text = year.ToString(CultureInfo.InvariantCulture) });
            }

            if (item.CommunityRating is { } rating)
            {
                descriptor.Badges.Add(new CardBadge { Kind = "RATING", Text = rating.ToString("0.0", CultureInfo.InvariantCulture) });
            }

            if (item.WholphinRating is { } wrating)
            {
                descriptor.Badges.Add(new CardBadge { Kind = "WRATING", Text = wrating.ToString("0.0", CultureInfo.InvariantCulture) });
            }

            foreach (var genre in CatalogFeatures.Parse(item.GenresJson).Take(2))
            {
                descriptor.Badges.Add(new CardBadge { Kind = "GENRE", Text = genre });
            }
        }

        // Requestable/discover items have no Jellyfin id for the client to resolve art from, so surface
        // the stored (TMDB) artwork directly. Library items keep these null → the client builds the
        // authenticated Jellyfin image URL from the media id (higher quality, no external dependency).
        if (item.JellyfinItemId is null)
        {
            var poster = string.IsNullOrEmpty(item.PosterImageUrl) ? null : item.PosterImageUrl;
            var backdrop = string.IsNullOrEmpty(item.BackdropImageUrl) ? null : item.BackdropImageUrl;
            var wide = type is CardType.Hero or CardType.BannerWide or CardType.Episode or CardType.NowPlaying;

            descriptor.BackdropImageUrl = backdrop;
            // Wide cards lead with 16:9 art (backdrop), tall cards with the poster — falling back to
            // whichever is present so the card is never blank.
            descriptor.ImageUrl = wide ? (backdrop ?? poster) : (poster ?? backdrop);
        }

        return descriptor;
    }

    private static string? Tagline(string? overview)
    {
        if (string.IsNullOrWhiteSpace(overview))
        {
            return null;
        }

        var trimmed = overview.Trim();
        return trimmed.Length <= TaglineMaxLength ? trimmed : trimmed[..TaglineMaxLength].TrimEnd() + "…";
    }

    private static string? ShortSynopsis(string? overview)
    {
        if (string.IsNullOrWhiteSpace(overview))
        {
            return null;
        }

        var trimmed = overview.Trim();
        return trimmed.Length <= SynopsisMaxLength ? trimmed : trimmed[..SynopsisMaxLength].TrimEnd() + "…";
    }

    // Row purposes whose cards render wide (16:9): For You, the discovery rows (You Might Like /
    // Because You Watched / country), Coming Soon, New Since Away.
    private static bool IsWidePurpose(string? purpose) => purpose switch
    {
        "recommended" or "discover" or "upcoming" or "newsince" => true,
        _ => false,
    };

    private static CardType ChooseType(CatalogItem item, CardSelectionContext ctx, bool requestable)
    {
        if (item.MediaType == MediaType.Person
            || string.Equals(ctx.RowPurpose, "people", StringComparison.OrdinalIgnoreCase))
        {
            return CardType.PersonCircle;
        }

        if (ctx.RowStyle == RowStyle.Top10)
        {
            return CardType.TopRanked;
        }

        if (ctx.RowStyle == RowStyle.Hero)
        {
            // Every item in the spotlight row is a Hero card (the client rotates through them).
            return CardType.Hero;
        }

        // Milestone 8: trailer-capable + requestable rows render as 16:9 landscape cards (so an inline
        // trailer fits the frame and the home mixes shapes). Applies to both available + requestable items.
        if (IsWidePurpose(ctx.RowPurpose))
        {
            return CardType.BannerWide;
        }

        if (requestable)
        {
            return CardType.Discover;
        }

        if (ctx.HasResume || string.Equals(ctx.RowPurpose, "continue", StringComparison.OrdinalIgnoreCase))
        {
            return CardType.BannerWide;
        }

        return item.MediaType switch
        {
            MediaType.Episode => CardType.Episode,
            MediaType.Season => CardType.Season,
            _ => CardType.PosterPortrait,
        };
    }

    private static CardType ApplyCapabilities(CardType type, bool requestable, ClientCapabilities caps)
    {
        // Empty list = unknown client → assume the v1 base set is renderable; only reserved types downgrade.
        var supported = caps.SupportedCardTypes is { Count: > 0 }
            ? new HashSet<CardType>(caps.SupportedCardTypes)
            : BaseV1Types;

        if (supported.Contains(type))
        {
            return type;
        }

        return requestable ? CardType.Discover : CardType.PosterPortrait;
    }

    private static CardImageType ImageFor(CardType type) => type switch
    {
        CardType.BannerWide or CardType.Episode or CardType.Hero or CardType.NowPlaying => CardImageType.Thumb,
        CardType.Logo => CardImageType.Logo,
        _ => CardImageType.Primary,
    };

    private static CardAspectRatio AspectFor(CardType type) => type switch
    {
        CardType.BannerWide or CardType.Episode or CardType.Hero or CardType.NowPlaying => CardAspectRatio.Wide,
        CardType.PersonCircle => CardAspectRatio.Square,
        _ => CardAspectRatio.Tall,
    };

    private static List<CardAction> ActionsFor(CatalogItem item, CardSelectionContext ctx)
    {
        var actions = new List<CardAction>();
        switch (item.Availability)
        {
            case AvailabilityState.WatchNow:
            case AvailabilityState.RecentlyAdded:
                actions.Add(ctx.HasResume ? CardAction.Resume : CardAction.Play);
                break;
            case AvailabilityState.Request:
                actions.Add(CardAction.Request);
                break;
        }

        actions.Add(CardAction.Details);
        return actions;
    }

    private static List<CardBadge> BadgesFor(CatalogItem item, CardSelectionContext ctx)
    {
        var badges = new List<CardBadge>();

        switch (item.Availability)
        {
            case AvailabilityState.Request:
                badges.Add(new CardBadge { Kind = "AVAILABILITY", Text = "REQUEST" });
                break;
            case AvailabilityState.Requested:
                badges.Add(new CardBadge { Kind = "AVAILABILITY", Text = "REQUESTED" });
                break;
            case AvailabilityState.Downloading:
                badges.Add(new CardBadge { Kind = "AVAILABILITY", Text = "DOWNLOADING" });
                break;
        }

        if (item.DateAdded is { } added && (DateTime.UtcNow - added).TotalDays <= NewItemDays)
        {
            badges.Add(new CardBadge { Kind = "NEW" });
        }

        if (ctx.RowStyle == RowStyle.Top10)
        {
            badges.Add(new CardBadge
            {
                Kind = "RANK",
                Text = (ctx.RankInRow + 1).ToString(CultureInfo.InvariantCulture),
            });
        }

        // Studio / streaming-provider brand tag (rendered as a top-left pill in the app). Prefer the TMDB
        // watch-provider brand (with a cached logo the app resolves via IconUrl); fall back to the first
        // Jellyfin studio name as text so nearly every card carries a tag.
        if (!string.IsNullOrWhiteSpace(item.ProviderBrandName))
        {
            badges.Add(new CardBadge
            {
                Kind = "STUDIO",
                Text = item.ProviderBrandName,
                IconUrl = item.ProviderBrandId is { } pid
                    ? $"/OrcaEngine/Images/Provider/{pid.ToString(CultureInfo.InvariantCulture)}"
                    : null,
            });
        }
        else if (CatalogFeatures.Parse(item.StudiosJson) is { Count: > 0 } studios)
        {
            badges.Add(new CardBadge { Kind = "STUDIO", Text = studios[0] });
        }

        return badges;
    }
}
