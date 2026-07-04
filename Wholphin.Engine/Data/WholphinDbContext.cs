using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Data;

/// <summary>
/// EF Core context for the Orca Engine's own SQLite database
/// (separate from Jellyfin's database).
/// </summary>
public class WholphinDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WholphinDbContext"/> class.
    /// </summary>
    public WholphinDbContext(DbContextOptions<WholphinDbContext> options)
        : base(options)
    {
    }

    /// <summary>Gets the unified catalog (available + requestable items).</summary>
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();

    /// <summary>Gets per-user personalization profiles.</summary>
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    /// <summary>Gets the append-only behavior event log.</summary>
    public DbSet<BehaviorEvent> BehaviorEvents => Set<BehaviorEvent>();

    /// <summary>Gets roaming per-user settings.</summary>
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();

    /// <summary>Gets incremental-sync cursors.</summary>
    public DbSet<SyncState> SyncStates => Set<SyncState>();

    /// <summary>Gets the durable log of engine-proxied media requests (schema v2).</summary>
    public DbSet<MediaRequest> MediaRequests => Set<MediaRequest>();

    /// <summary>Gets the per-user justification records for currently-recommended external titles (schema v7).</summary>
    public DbSet<UserDiscoveryPick> UserDiscoveryPicks => Set<UserDiscoveryPick>();

    /// <summary>Gets the longitudinal per-(user, title) recommendation memory (schema v7).</summary>
    public DbSet<UserItemMemory> UserItemMemories => Set<UserItemMemory>();

    /// <summary>Gets the per-pull discovery funnel reports (schema v7).</summary>
    public DbSet<DiscoveryRun> DiscoveryRuns => Set<DiscoveryRun>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CatalogItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.JellyfinItemId);
            e.HasIndex(x => x.TmdbId);
            e.HasIndex(x => x.ImdbId);
            e.HasIndex(x => x.MediaType);
            e.HasIndex(x => x.Availability);
            e.Property(x => x.Title).HasMaxLength(1024);
        });

        modelBuilder.Entity<UserProfile>(e =>
        {
            e.HasKey(x => x.UserId);
        });

        modelBuilder.Entity<BehaviorEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Timestamp });
            e.HasIndex(x => x.EventType);
        });

        modelBuilder.Entity<UserSetting>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Key });
        });

        modelBuilder.Entity<SyncState>(e =>
        {
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<MediaRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<UserDiscoveryPick>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Kind });
            e.HasIndex(x => x.CatalogItemId);
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => new { x.Kind, x.Country });
        });

        modelBuilder.Entity<UserItemMemory>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.TmdbId, x.MediaType }).IsUnique();
            e.HasIndex(x => x.UpdatedAt);
        });

        modelBuilder.Entity<DiscoveryRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.StartedAt });
            e.HasIndex(x => x.StartedAt);
        });
    }
}
