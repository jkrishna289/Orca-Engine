using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Metadata;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The identity every provider is keyed on. Providers need DIFFERENT ids — OMDb needs IMDb, Fanart
/// needs TVDB for series — so resolving them once is what makes a second provider reachable at all.
/// </summary>
public class MediaIdentityTests
{
    [Fact]
    public void FromCatalogItem_CarriesEveryId()
    {
        var identity = MediaIdentity.FromCatalogItem(new CatalogItem
        {
            TmdbId = 157336,
            ImdbId = "tt0816692",
            TvdbId = 4242,
            MediaType = MediaType.Movie,
            Title = "Interstellar",
            OriginalTitle = "Interstellar",
            ProductionYear = 2014,
        });

        Assert.Equal(157336, identity.TmdbId);
        Assert.Equal("tt0816692", identity.ImdbId);
        Assert.Equal(4242, identity.TvdbId);
        Assert.Equal(2014, identity.Year);
        Assert.True(identity.HasAnyId);
    }

    [Fact]
    public void BlankImdbId_BecomesNull_SoProvidersCanTestItWithoutTrimming()
    {
        var identity = MediaIdentity.FromCatalogItem(new CatalogItem { ImdbId = "   ", Title = "X" });
        Assert.Null(identity.ImdbId);
    }

    [Fact]
    public void HasAnyId_IsFalseWhenNothingIsKnown()
    {
        Assert.False(MediaIdentity.FromCatalogItem(new CatalogItem { Title = "Unmatched" }).HasAnyId);
    }

    [Fact]
    public void CacheKey_DistinguishesMediaTypeAndIds_ButNeverCarriesTheTitle()
    {
        // Cache keys become metric namespaces elsewhere; free text would make the key space unbounded.
        var movie = new MediaIdentity(1, null, null, null, MediaType.Movie, "Ambiguous Title", null, 2020);
        var series = new MediaIdentity(1, null, null, null, MediaType.Series, "Ambiguous Title", null, 2020);

        Assert.NotEqual(movie.CacheKey, series.CacheKey);
        Assert.DoesNotContain("Ambiguous", movie.CacheKey, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheKey_IsStableForTheSameIds()
    {
        var a = new MediaIdentity(1, "tt1", 2, null, MediaType.Movie, "A", null, 2020);
        var b = new MediaIdentity(1, "tt1", 2, null, MediaType.Movie, "Renamed Later", "orig", 2021);

        // Titles and years drift as providers disagree; the ids are the identity.
        Assert.Equal(a.CacheKey, b.CacheKey);
    }
}
