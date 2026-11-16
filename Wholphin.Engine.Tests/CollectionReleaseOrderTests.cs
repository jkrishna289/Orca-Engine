using System.Linq;
using Wholphin.Engine.Controllers;
using Wholphin.Engine.Data.Entities;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// A collection shelf numbers its films, so the order is the watch order: release year, and a film
/// TMDB has announced without a date goes last rather than first.
/// </summary>
public class CollectionReleaseOrderTests
{
    [Fact]
    public void FilmsRunInReleaseOrderWithAnnouncedOnesLast()
    {
        var films = new[]
        {
            new CatalogItem { Title = "Spider-Man: No Way Home", ProductionYear = 2021 },
            new CatalogItem { Title = "Spider-Man 4", ProductionYear = null },
            new CatalogItem { Title = "Spider-Man: Homecoming", ProductionYear = 2017 },
            new CatalogItem { Title = "Spider-Man: Far From Home", ProductionYear = 2019 },
        };

        Assert.Equal(
            new[] { "Spider-Man: Homecoming", "Spider-Man: Far From Home", "Spider-Man: No Way Home", "Spider-Man 4" },
            CollectionsController.ReleaseOrder(films).Select(f => f.Title));
    }
}
