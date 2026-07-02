using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Presentation;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Card-layout endpoints. <c>Cards/Preview</c> serves a real <see cref="RenderBundle"/> built from
/// synthetic items via the live <see cref="ICardSelector"/>, so the app can build against the
/// contract before the sync engine and recommender exist. (Dev/preview — to be secured/removed later.)
/// </summary>
[ApiController]
[Route("OrcaEngine")]
[Produces("application/json")]
public class CardsController : ControllerBase
{
    private readonly ICardSelector _cardSelector;

    /// <summary>
    /// Initializes a new instance of the <see cref="CardsController"/> class.
    /// </summary>
    public CardsController(ICardSelector cardSelector) => _cardSelector = cardSelector;

    /// <summary>
    /// Returns a sample render bundle exercising the dynamic card selector across row contexts.
    /// </summary>
    /// <param name="supported">
    /// Optional comma-separated CardType names the client supports (to test capability gating),
    /// e.g. <c>?supported=PosterPortrait,BannerWide,Discover</c>. Omit = full v1 set.
    /// </param>
    [HttpGet("Cards/Preview")]
    [AllowAnonymous]
    public ActionResult<RenderBundle> GetPreview([FromQuery] string? supported = null)
    {
        var caps = ParseCapabilities(supported);

        return new RenderBundle
        {
            ContractVersion = 1,
            Rows =
            {
                BuildRow("continue", "Continue Watching", RowStyle.Standard, "continue", caps, new[]
                {
                    Resume(Movie("The Martian", 286217, available: true)),
                    Resume(Episode("Stranger Things", "Chapter Three")),
                }),
                BuildRow("scifi", "Because You Love Sci-Fi", RowStyle.Standard, "genre", caps, new[]
                {
                    Plain(Movie("Blade Runner 2049", 335984, available: true)),
                    Plain(Movie("Dune: Part Two", 693134, available: false, availability: AvailabilityState.Request)),
                    Plain(Movie("Arrival", 329865, available: true)),
                    Plain(Movie("Foundation", 93740, available: false, availability: AvailabilityState.Requested)),
                }),
                BuildRow("top10", "Top 10 Today", RowStyle.Top10, "trending", caps, new[]
                {
                    Plain(Movie("Oppenheimer", 872585, available: true)),
                    Plain(Movie("Barbie", 346698, available: true)),
                    Plain(Movie("Poor Things", 792307, available: false, availability: AvailabilityState.Request)),
                }),
                BuildRow("people", "Cast & Crew", RowStyle.Circle, "people", caps, new[]
                {
                    Plain(Person("Denis Villeneuve")),
                    Plain(Person("Ryan Gosling")),
                }),
            },
        };
    }

    private RenderRow BuildRow(
        string id,
        string title,
        RowStyle style,
        string purpose,
        ClientCapabilities caps,
        IReadOnlyList<(CatalogItem Item, bool HasResume)> entries)
    {
        var row = new RenderRow { Id = id, Title = title, RowStyle = style };
        for (var i = 0; i < entries.Count; i++)
        {
            var (item, hasResume) = entries[i];
            var card = _cardSelector.Select(item, new CardSelectionContext
            {
                RowPurpose = purpose,
                RowStyle = style,
                RankInRow = i,
                HasResume = hasResume,
                Capabilities = caps,
            });

            if (hasResume)
            {
                card.Progress = 0.4; // demo value; real progress comes from user data at the home stage
            }

            // Preview-only placeholder art; real items resolve image URLs at the sync/home stage.
            card.ImageUrl = $"https://placehold.co/300x450/141414/E6E6E6?text={Uri.EscapeDataString(item.Title)}";

            row.Items.Add(new RenderItem { Media = MediaIdMapper.ToMediaId(item), Card = card });
        }

        return row;
    }

    private static ClientCapabilities ParseCapabilities(string? supported)
    {
        var caps = new ClientCapabilities();
        if (string.IsNullOrWhiteSpace(supported))
        {
            return caps;
        }

        foreach (var token in supported.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<CardType>(token, ignoreCase: true, out var cardType))
            {
                caps.SupportedCardTypes.Add(cardType);
            }
        }

        return caps;
    }

    private static (CatalogItem, bool) Resume(CatalogItem item) => (item, true);

    private static (CatalogItem, bool) Plain(CatalogItem item) => (item, false);

    private static CatalogItem Movie(string title, int tmdb, bool available, AvailabilityState availability = AvailabilityState.WatchNow) => new()
    {
        Title = title,
        MediaType = MediaType.Movie,
        TmdbId = tmdb == 0 ? null : tmdb,
        JellyfinItemId = available ? Guid.NewGuid() : null,
        Availability = available ? AvailabilityState.WatchNow : availability,
        DateAdded = DateTime.UtcNow.AddDays(-3),
        LastSyncedAt = DateTime.UtcNow,
    };

    private static CatalogItem Episode(string series, string title) => new()
    {
        Title = title,
        OriginalTitle = series,
        MediaType = MediaType.Episode,
        JellyfinItemId = Guid.NewGuid(),
        Availability = AvailabilityState.WatchNow,
        LastSyncedAt = DateTime.UtcNow,
    };

    private static CatalogItem Person(string name) => new()
    {
        Title = name,
        MediaType = MediaType.Person,
        JellyfinItemId = Guid.NewGuid(),
        Availability = AvailabilityState.WatchNow,
        LastSyncedAt = DateTime.UtcNow,
    };
}
