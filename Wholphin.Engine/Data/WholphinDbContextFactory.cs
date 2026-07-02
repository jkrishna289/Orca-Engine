using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Wholphin.Engine.Data;

/// <summary>
/// Builds <see cref="WholphinDbContext"/> instances pointing at the engine's own
/// SQLite file under the Jellyfin data directory.
/// </summary>
public class WholphinDbContextFactory : IWholphinDbContextFactory
{
    private readonly string _dbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="WholphinDbContextFactory"/> class.
    /// </summary>
    public WholphinDbContextFactory(IApplicationPaths applicationPaths)
    {
        var dir = Path.Combine(applicationPaths.DataPath, "wholphin-engine");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "wholphin-engine.db");
    }

    /// <inheritdoc />
    public WholphinDbContext Create()
    {
        var options = new DbContextOptionsBuilder<WholphinDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new WholphinDbContext(options);
    }
}
