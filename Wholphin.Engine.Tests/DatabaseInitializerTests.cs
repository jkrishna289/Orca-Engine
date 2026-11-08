using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Xunit;

namespace Wholphin.Engine.Tests;

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestDbFactory _factory;

    public DatabaseInitializerTests()
    {
        _connection.Open();
        _factory = new TestDbFactory(_connection);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task AColumnMissingFromADatabaseAlreadyAtTheCurrentVersionIsStillAdded()
    {
        // 1.4.13.0 on a real server: the database was already at the schema version, the new
        // VoteCount column was only added on a version bump, and every catalog query then failed.
        await using (var db = _factory.Create())
        {
            await db.Database.EnsureCreatedAsync();
            db.CatalogItems.Add(new CatalogItem { Title = "Existing title", JellyfinItemId = Guid.NewGuid() });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE CatalogItems DROP COLUMN VoteCount");
            await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version = {DatabaseInitializer.SchemaVersion}");
        }

        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var after = _factory.Create();
        var item = Assert.Single(await after.CatalogItems.ToListAsync());
        Assert.Null(item.VoteCount);
    }
}
