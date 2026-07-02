namespace Wholphin.Engine.Data;

/// <summary>
/// Creates short-lived <see cref="WholphinDbContext"/> instances (thread-safe for
/// use by singleton services and background workers).
/// </summary>
public interface IWholphinDbContextFactory
{
    /// <summary>Creates a new database context.</summary>
    WholphinDbContext Create();
}
