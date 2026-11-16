using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Catalog;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Presentation;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// The collections a title belongs to, as shelves for its details page: first the film's TMDB
/// collection (every film in it, the ones you don't have included so they can be requested), then
/// each Jellyfin collection (box set) holding the title, smart or hand-made. Watched state is the
/// signed-in user's, so the endpoint needs a session.
/// </summary>
[ApiController]
[Authorize]
[Route("OrcaEngine")]
[Produces("application/json")]
public sealed class CollectionsController : ControllerBase
{
    // A TMDB collection gains a film every few years; a page visit shouldn't cost two TMDB calls.
    private static readonly TimeSpan TmdbTtl = TimeSpan.FromHours(12);

    private readonly ITmdbClient _tmdb;
    private readonly ICardSelector _cardSelector;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IAuthorizationContext _authorization;
    private readonly ICache _cache;

    /// <summary>Initializes a new instance of the <see cref="CollectionsController"/> class.</summary>
    /// <param name="tmdb">TMDB, for the film's collection.</param>
    /// <param name="cardSelector">Picks each film's card.</param>
    /// <param name="factory">The database context factory.</param>
    /// <param name="library">Jellyfin's library, for the box sets.</param>
    /// <param name="users">Jellyfin's users.</param>
    /// <param name="userData">Jellyfin's per-user watched state.</param>
    /// <param name="authorization">Who is asking.</param>
    /// <param name="cache">Holds TMDB answers.</param>
    public CollectionsController(
        ITmdbClient tmdb,
        ICardSelector cardSelector,
        IWholphinDbContextFactory factory,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        IAuthorizationContext authorization,
        ICache cache)
    {
        _tmdb = tmdb;
        _cardSelector = cardSelector;
        _factory = factory;
        _library = library;
        _users = users;
        _userData = userData;
        _authorization = authorization;
        _cache = cache;
    }

    /// <summary>The shelves for a title, its TMDB collection first. Empty when it is in none.</summary>
    /// <param name="jellyfinId">The title in the library.</param>
    /// <param name="tmdbId">A film's TMDB id, for a film outside the library (ignored when <paramref name="jellyfinId"/> is given).</param>
    /// <param name="supported">Card types the client can render (comma-separated).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shelves.</returns>
    [HttpGet("Collections")]
    public async Task<ActionResult<CollectionsResponse>> GetCollections(
        [FromQuery] Guid? jellyfinId,
        [FromQuery] int? tmdbId,
        [FromQuery] string? supported,
        CancellationToken cancellationToken = default)
    {
        var userId = (await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).UserId;
        var user = userId == Guid.Empty ? null : _users.GetUserById(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        var item = jellyfinId is { } jid && jid != Guid.Empty ? _library.GetItemById(jid) : null;
        if (item is not null && !item.IsVisible(user, false))
        {
            item = null;
        }

        var capabilities = SimilarController.ParseCapabilities(supported);
        var response = new CollectionsResponse();
        await using var db = _factory.Create();

        // The film's TMDB collection: Jellyfin's metadata names it for a library film; a film outside
        // the library asks TMDB.
        var collectionId = item is Movie
            ? ParseId(item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.TmdbCollection))
            : item is null && tmdbId is > 0 ? await _tmdb.GetCollectionIdAsync(tmdbId.Value, cancellationToken).ConfigureAwait(false) : null;

        var franchise = collectionId is { } cid
            ? await CachedAsync($"tmdb:collection:{cid}", () => _tmdb.GetCollectionAsync(cid, cancellationToken)).ConfigureAwait(false)
            : null;
        if (franchise is { Parts.Count: > 1 })
        {
            var rows = await TmdbTitleRows.MatchOrImportAsync(db, franchise.Parts, franchise.Parts.Count, cancellationToken).ConfigureAwait(false);
            var films = franchise.Parts
                .Select(p => rows.GetValueOrDefault((p.TmdbId, p.MediaType)))
                .OfType<CatalogItem>()
                .DistinctBy(c => c.Id)
                .ToList();
            AddShelf(response, "tmdb:" + franchise.Id.ToString(CultureInfo.InvariantCulture), franchise.Name, franchise.BackdropImageUrl, true, films, user, capabilities);
        }

        // Every other collection holding the title. The server's own box set for the TMDB collection
        // (when its automatic collections are on) is the shelf above, not a second one.
        if (item is not null)
        {
            var franchiseKey = collectionId?.ToString(CultureInfo.InvariantCulture);
            var sets = _library.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true })
                .OfType<BoxSet>()
                .Where(b => b.ContainsLinkedChildByItemId(item.Id))
                .Where(b => franchiseKey is null || b.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb) != franchiseKey)
                .ToList();
            var members = sets.ToDictionary(b => b, b => b.GetLinkedChildren().Where(c => c.IsVisible(user, false)).Select(c => c.Id).ToList());
            var ids = members.Values.SelectMany(x => x).Distinct().ToList();
            var byJellyfinId = ids.Count == 0
                ? new Dictionary<Guid, CatalogItem>()
                : (await db.CatalogItems.AsNoTracking()
                    .Where(c => c.JellyfinItemId != null && ids.Contains(c.JellyfinItemId.Value))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                    .GroupBy(c => c.JellyfinItemId!.Value)
                    .ToDictionary(g => g.Key, g => g.First());
            foreach (var (set, kids) in members)
            {
                var films = kids.Select(k => byJellyfinId.GetValueOrDefault(k)).OfType<CatalogItem>().ToList();
                var backdrop = set.HasImage(ImageType.Backdrop, 0) ? $"/Items/{set.Id:N}/Images/Backdrop?fillHeight=720&quality=90" : null;
                AddShelf(response, set.Id.ToString("N"), set.Name ?? string.Empty, backdrop, false, films, user, capabilities);
            }
        }

        return response;
    }

    /// <summary>Films in release order, ones with no date yet (announced) last.</summary>
    /// <param name="films">The shelf's films.</param>
    /// <returns>The same films, ordered.</returns>
    internal static List<CatalogItem> ReleaseOrder(IEnumerable<CatalogItem> films) =>
        films.OrderBy(f => f.ProductionYear ?? int.MaxValue).ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ToList();

    private static int? ParseId(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private async Task<T?> CachedAsync<T>(string key, Func<Task<T?>> load)
        where T : class
    {
        if (_cache.TryGet<T>(key, out var hit))
        {
            return hit;
        }

        var value = await load().ConfigureAwait(false);
        if (value is not null)
        {
            _cache.Set(key, value, TmdbTtl);
        }

        return value;
    }

    private void AddShelf(CollectionsResponse response, string id, string name, string? backdrop, bool franchise, List<CatalogItem> films, User user, ClientCapabilities capabilities)
    {
        if (films.Count < 2)
        {
            return; // a collection of one is just this title
        }

        var ordered = ReleaseOrder(films);
        var shelf = new CollectionShelf
        {
            Id = id,
            Name = name,
            Franchise = franchise,
            BackdropImageUrl = backdrop ?? ordered.Select(f => f.BackdropImageUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
        };
        for (var i = 0; i < ordered.Count; i++)
        {
            var film = ordered[i];
            var card = _cardSelector.Select(film, new CardSelectionContext
            {
                RowPurpose = "collection",
                RowStyle = RowStyle.Standard,
                RankInRow = i,
                HasResume = false,
                Capabilities = capabilities,
            });
            var entry = new CollectionEntry
            {
                Item = new RenderItem { Media = MediaIdMapper.ToMediaId(film), Card = card },
                Year = film.ProductionYear,
                PosterImageUrl = film.PosterImageUrl,
            };
            if (film.JellyfinItemId is { } jid && _library.GetItemById(jid) is { } owned)
            {
                var data = _userData.GetUserData(user, owned);
                var position = data?.PlaybackPositionTicks ?? 0;
                entry.Played = data?.Played == true;
                entry.PlayedPercentage = !entry.Played && position > 0 && owned.RunTimeTicks is long runtime && runtime > 0
                    ? Math.Clamp(100.0 * position / runtime, 0, 100)
                    : null;
            }

            shelf.Items.Add(entry);
        }

        response.Shelves.Add(shelf);
    }

    /// <summary>A title's collection shelves.</summary>
    public sealed class CollectionsResponse
    {
        /// <summary>Gets the shelves, the TMDB collection first.</summary>
        public List<CollectionShelf> Shelves { get; } = new();
    }

    /// <summary>One collection: its name, backdrop and titles in release order.</summary>
    public sealed class CollectionShelf
    {
        /// <summary>Gets or sets a stable id ("tmdb:{id}" for a TMDB collection, else the box set's id).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the name as TMDB or the server has it ("Spider-Man (Avengers) Collection").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets a value indicating whether this is a TMDB collection, which has a watch order.</summary>
        public bool Franchise { get; set; }

        /// <summary>Gets or sets the backdrop to show behind the shelf.</summary>
        public string? BackdropImageUrl { get; set; }

        /// <summary>Gets the titles in release order.</summary>
        public List<CollectionEntry> Items { get; } = new();
    }

    /// <summary>One title on a shelf.</summary>
    public sealed class CollectionEntry
    {
        /// <summary>Gets or sets the title as a card; its media id carries the library id when you have it.</summary>
        public RenderItem Item { get; set; } = new();

        /// <summary>Gets or sets the release year.</summary>
        public int? Year { get; set; }

        /// <summary>Gets or sets the poster.</summary>
        public string? PosterImageUrl { get; set; }

        /// <summary>Gets or sets a value indicating whether the user has watched it.</summary>
        public bool Played { get; set; }

        /// <summary>Gets or sets how far into it the user is (0-100), when started and not finished.</summary>
        public double? PlayedPercentage { get; set; }
    }
}
