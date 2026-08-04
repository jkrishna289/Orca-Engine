namespace Wholphin.Engine.Data;

/// <summary>
/// Creates short-lived <see cref="WholphinDbContext"/> instances (thread-safe for
/// use by singleton services and background workers).
/// </summary>
public interface IWholphinDbContextFactory
{
    /// <summary>
    /// Gets the full path to the engine's SQLite file.
    /// </summary>
    /// <remarks>
    /// Exposed so the dashboard can report the file's size. Of everything an admin can see, this is
    /// the number that actually grows without bound — unlike "plugin memory", which cannot be
    /// measured at all from inside a shared process.
    /// </remarks>
    string DatabasePath { get; }

    /// <summary>Creates a new database context.</summary>
    WholphinDbContext Create();
}
