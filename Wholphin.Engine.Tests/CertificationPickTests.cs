using System.Collections.Generic;
using Wholphin.Engine.Integrations.Tmdb;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The age rating shown for a title outside the library. Wrong here is quiet: a viewer sees another
/// country's rating, or none, and nothing fails.
/// </summary>
public class CertificationPickTests
{
    private static TmdbDetail Movie(params (string Country, string[] Certs)[] countries)
    {
        var results = new List<TmdbReleaseDates>();
        foreach (var (country, certs) in countries)
        {
            var dates = new List<TmdbReleaseDate>();
            foreach (var c in certs)
            {
                dates.Add(new TmdbReleaseDate { Certification = c });
            }

            results.Add(new TmdbReleaseDates { Country = country, Dates = dates });
        }

        return new TmdbDetail { ReleaseDates = new TmdbCountryList<TmdbReleaseDates> { Results = results } };
    }

    [Fact]
    public void UsesTheConfiguredRegionsFirstNonEmptyCertification()
    {
        // Most releases carry an empty certification; the first real one wins.
        var detail = Movie(("US", new[] { "PG-13" }), ("GB", new[] { "", "15" }));

        Assert.Equal("15", TmdbClient.PickCertification(detail, "gb"));
    }

    [Fact]
    public void FallsBackToTheUsRatingWhenTheRegionHasNone()
    {
        var detail = Movie(("US", new[] { "R" }), ("MU", new[] { "" }));

        Assert.Equal("R", TmdbClient.PickCertification(detail, "MU"));
    }

    [Fact]
    public void ReadsSeriesContentRatings()
    {
        var detail = new TmdbDetail
        {
            ContentRatings = new TmdbCountryList<TmdbContentRating>
            {
                Results = new List<TmdbContentRating> { new() { Country = "US", Rating = "TV-MA" } },
            },
        };

        Assert.Equal("TV-MA", TmdbClient.PickCertification(detail, null));
    }

    [Fact]
    public void NullWhenNoCountryHasOne()
    {
        Assert.Null(TmdbClient.PickCertification(Movie(("US", new[] { "" })), "US"));
    }
}
