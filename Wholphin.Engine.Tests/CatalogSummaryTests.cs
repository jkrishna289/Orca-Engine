using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Controllers;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The one query in the admin surface that has to run in SQL rather than in memory: Status is
/// polled, so it must not read the whole catalog. EF Core throws at RUNTIME when it can't translate
/// a GroupBy, and no other test in this project touches a database — so this is the check that the
/// grouping actually reaches SQLite instead of silently regressing to a full table read.
/// </summary>
public class CatalogSummaryTests
{
    [Fact]
    public async Task Summary_CountsByTypeAvailabilityAndOrigin()
    {
        await using var db = await NewDatabaseAsync();
        db.CatalogItems.AddRange(
            Item(MediaType.Movie, AvailabilityState.WatchNow, external: false),
            Item(MediaType.Movie, AvailabilityState.WatchNow, external: false),
            Item(MediaType.Movie, AvailabilityState.Request, external: true),
            Item(MediaType.Series, AvailabilityState.WatchNow, external: false),
            Item(MediaType.Series, AvailabilityState.Request, external: true),
            Item(MediaType.Series, AvailabilityState.Request, external: true));
        await db.SaveChangesAsync();

        var summary = await AdminController.LoadCatalogSummaryAsync(db, CancellationToken.None);

        Assert.Equal(6, summary.Total);
        Assert.Equal(3, summary.ExternalRows);
        Assert.Equal(3, summary.ByType["Movie"]);
        Assert.Equal(3, summary.ByType["Series"]);
        Assert.Equal(3, summary.ByAvailability["WatchNow"]);
        Assert.Equal(3, summary.ByAvailability["Request"]);
    }

    [Fact]
    public async Task Summary_OnEmptyCatalog_IsAllZero()
    {
        await using var db = await NewDatabaseAsync();

        var summary = await AdminController.LoadCatalogSummaryAsync(db, CancellationToken.None);

        Assert.Equal(0, summary.Total);
        Assert.Equal(0, summary.ExternalRows);
        Assert.Empty(summary.ByType);
        Assert.Empty(summary.ByAvailability);
    }

    /// <summary>
    /// A real SQLite database (in memory, but a genuine provider) — the point of these tests is that
    /// the LINQ translates, which the EF in-memory provider would happily fake.
    /// </summary>
    private static async Task<WholphinDbContext> NewDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WholphinDbContext>().UseSqlite(connection).Options;
        var db = new WholphinDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static CatalogItem Item(MediaType type, AvailabilityState availability, bool external) => new()
    {
        MediaType = type,
        Availability = availability,
        JellyfinItemId = external ? null : Guid.NewGuid(),
        Title = "t",
    };
}
