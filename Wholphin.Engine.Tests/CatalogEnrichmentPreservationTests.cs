using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Sync;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// A library sync must refresh what Jellyfin knows without discarding what the engine learned.
/// </summary>
/// <remarks>
/// Both upsert paths apply a mapped row with <c>CurrentValues.SetValues</c>, which copies every
/// scalar property including nulls, while <see cref="CatalogMapper.Map"/> only fills the fields
/// Jellyfin supplies. The gap between those two sets is exactly what
/// <see cref="CatalogMapper.PreserveEnrichment"/> exists to carry across, and it is silent when it
/// regresses: nothing throws, the row just quietly loses its enrichment and gets re-fetched.
/// </remarks>
public class CatalogEnrichmentPreservationTests
{
    /// <summary>A row that has been through every enricher.</summary>
    private static CatalogItem Enriched() => new()
    {
        Id = 42,
        JellyfinItemId = Guid.NewGuid(),
        TmdbId = 157336,
        Title = "Interstellar",
        MediaType = MediaType.Movie,
        Availability = AvailabilityState.WatchNow,

        OriginalLanguage = "en",
        CollectionName = "Nolan Collection",
        PosterImageUrl = "https://image.tmdb.org/poster.jpg",
        BackdropImageUrl = "https://image.tmdb.org/backdrop.jpg",
        TrailerUrl = "https://www.youtube.com/watch?v=abc",
        ProviderBrandId = 8,
        ProviderBrandName = "Netflix",
        ProvidersSyncedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ContentWarningsJson = "[\"flashing lights\"]",
        ContentWarningsSyncedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        LogoImageUrl = "https://assets.fanart.tv/logo.png",
        RatingsJson = "{\"imdb\":86,\"rt\":73}",
        MetadataSourcesJson = "{\"Poster\":\"fanart\",\"Ratings\":\"omdb\"}",
        MetadataSyncedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
        WholphinRating = 8.4,
        WholphinVotes = 17,
        FeatureVectorJson = "[0.1,0.2]",
        DateAdded = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>What CatalogMapper.Map produces — only the fields Jellyfin supplies.</summary>
    private static CatalogItem FreshlyMapped(CatalogItem existing) => new()
    {
        JellyfinItemId = existing.JellyfinItemId,
        TmdbId = existing.TmdbId,
        Title = "Interstellar",
        MediaType = MediaType.Movie,
        Availability = AvailabilityState.WatchNow,
        Overview = "A team travels through a wormhole.",
        LastSyncedAt = DateTime.UtcNow,
    };

    [Fact]
    public void SyncDoesNotErase_TheStampsThatStopEnrichersRefetching()
    {
        // The expensive half: these two stamps gate the enrichers' candidate queries, so clearing
        // them re-queues the row on every sync — re-spending TMDB quota and paid LLM calls.
        var existing = Enriched();
        var mapped = FreshlyMapped(existing);

        CatalogMapper.PreserveEnrichment(mapped, existing);

        Assert.Equal(existing.ProvidersSyncedAt, mapped.ProvidersSyncedAt);
        Assert.Equal(existing.ContentWarningsSyncedAt, mapped.ContentWarningsSyncedAt);

        // MetadataSyncedAt gates the multi-provider enricher the same way, and OMDb's free tier is
        // 1000 requests a day — re-queuing every row on every sync would burn it on already-done work.
        Assert.Equal(existing.MetadataSyncedAt, mapped.MetadataSyncedAt);
    }

    [Fact]
    public void SyncPreservesEveryFieldTheMapperDoesNotOwn()
    {
        var existing = Enriched();
        var mapped = FreshlyMapped(existing);

        CatalogMapper.PreserveEnrichment(mapped, existing);

        Assert.Equal(existing.OriginalLanguage, mapped.OriginalLanguage);
        Assert.Equal(existing.CollectionName, mapped.CollectionName);
        Assert.Equal(existing.PosterImageUrl, mapped.PosterImageUrl);
        Assert.Equal(existing.BackdropImageUrl, mapped.BackdropImageUrl);
        Assert.Equal(existing.TrailerUrl, mapped.TrailerUrl);
        Assert.Equal(existing.ProviderBrandId, mapped.ProviderBrandId);
        Assert.Equal(existing.ProviderBrandName, mapped.ProviderBrandName);
        Assert.Equal(existing.ContentWarningsJson, mapped.ContentWarningsJson);
        Assert.Equal(existing.WholphinRating, mapped.WholphinRating);
        Assert.Equal(existing.WholphinVotes, mapped.WholphinVotes);
        Assert.Equal(existing.FeatureVectorJson, mapped.FeatureVectorJson);
        Assert.Equal(existing.DateAdded, mapped.DateAdded);
        Assert.Equal(existing.LogoImageUrl, mapped.LogoImageUrl);
        Assert.Equal(existing.RatingsJson, mapped.RatingsJson);
        Assert.Equal(existing.MetadataSourcesJson, mapped.MetadataSourcesJson);
    }

    [Fact]
    public void JellyfinOwnedFieldsStillWin()
    {
        // Preservation must not become "never update anything" — the whole point of a sync is that
        // Jellyfin's view of title, overview and availability is the newer one.
        var existing = Enriched();
        existing.Overview = "stale overview";
        existing.Title = "stale title";

        var mapped = FreshlyMapped(existing);
        CatalogMapper.PreserveEnrichment(mapped, existing);

        Assert.Equal("A team travels through a wormhole.", mapped.Overview);
        Assert.Equal("Interstellar", mapped.Title);
    }

    [Fact]
    public void EveryUnmappedColumnIsAccountedFor()
    {
        // The guard against the failure that started this: someone adds a column to CatalogItem,
        // nothing reminds them to preserve it, and a sync silently begins erasing it. Every
        // property must be either written by the mapper or preserved here.
        var mapperOwns = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(CatalogItem.Id), nameof(CatalogItem.JellyfinItemId), nameof(CatalogItem.TmdbId),
            nameof(CatalogItem.ImdbId), nameof(CatalogItem.TvdbId), nameof(CatalogItem.MediaType),
            nameof(CatalogItem.Availability), nameof(CatalogItem.Title), nameof(CatalogItem.OriginalTitle),
            nameof(CatalogItem.ProductionYear), nameof(CatalogItem.PremiereDate), nameof(CatalogItem.RuntimeMinutes),
            nameof(CatalogItem.OfficialRating), nameof(CatalogItem.CommunityRating), nameof(CatalogItem.CriticRating),
            nameof(CatalogItem.Overview), nameof(CatalogItem.GenresJson), nameof(CatalogItem.StudiosJson),
            nameof(CatalogItem.TagsJson), nameof(CatalogItem.PeopleJson), nameof(CatalogItem.LastSyncedAt),
        };

        // A distinctive value per property, so "was it carried across" is observable generically.
        var existing = Enriched();
        var mapped = FreshlyMapped(existing);
        CatalogMapper.PreserveEnrichment(mapped, existing);

        var unaccounted = typeof(CatalogItem).GetProperties()
            .Where(p => p.CanWrite && !mapperOwns.Contains(p.Name))
            .Where(p =>
            {
                var kept = p.GetValue(mapped);
                var was = p.GetValue(existing);
                return !Equals(kept, was);
            })
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            unaccounted.Count == 0,
            $"CatalogItem columns neither mapped nor preserved — a sync will erase these: {string.Join(", ", unaccounted)}");
    }
}
