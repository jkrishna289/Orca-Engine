using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wholphin.Engine.Data;

/// <summary>
/// Creates the engine database (schema + WAL journaling) on server startup, then applies any
/// lightweight schema migrations. EnsureCreated builds the schema for a brand-new database; for an
/// existing one (which EnsureCreated leaves untouched) the migration runner adds new tables/columns
/// idempotently, tracked by SQLite's <c>PRAGMA user_version</c>. (Full EF migrations can replace
/// this later; this keeps the plugin deploy-as-DLL-only.)
/// </summary>
public class DatabaseInitializer : IHostedService
{
    /// <summary>The schema version this build expects.</summary>
    public const int SchemaVersion = 12;

    /// <summary>
    /// Idempotent migration steps keyed by the version they bring the schema TO. Each statement is
    /// safe to re-run (CREATE TABLE/INDEX IF NOT EXISTS), so applying them on a fresh DB (where
    /// EnsureCreated already made the tables) is a harmless no-op.
    /// </summary>
    private static readonly IReadOnlyList<(int Version, string Sql)> Migrations = new[]
    {
        (2,
            """
            CREATE TABLE IF NOT EXISTS "MediaRequests" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_MediaRequests" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "TmdbId" INTEGER NOT NULL,
                "MediaType" INTEGER NOT NULL,
                "Title" TEXT NULL,
                "Availability" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_MediaRequests_CreatedAt" ON "MediaRequests" ("CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_MediaRequests_UserId" ON "MediaRequests" ("UserId");
            """),
        // v7 — justified discovery: pick/memory/run tables, plus a one-time purge of external
        // Availability=Request rows left behind by the old blind discovery import. The DELETE only
        // touches rows nothing references (no request by TmdbId, no behavior event, no pick), so
        // re-running it is safe; in-flight rows (Requested/Downloading/RecentlyAdded) have a
        // different Availability value and are untouched by construction.
        (7,
            """
            CREATE TABLE IF NOT EXISTS "UserDiscoveryPicks" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UserDiscoveryPicks" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "CatalogItemId" INTEGER NOT NULL,
                "Kind" INTEGER NOT NULL,
                "SourceType" TEXT NULL,
                "Reason" TEXT NULL,
                "SeedTmdbId" INTEGER NULL,
                "SeedTitle" TEXT NULL,
                "Country" TEXT NULL,
                "FinalScore" REAL NOT NULL,
                "TasteScore" REAL NOT NULL,
                "PopularityScore" REAL NOT NULL,
                "FreshnessScore" REAL NOT NULL,
                "ScoreExplanationJson" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ExpiresAt" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_UserDiscoveryPicks_UserId_Kind" ON "UserDiscoveryPicks" ("UserId", "Kind");
            CREATE INDEX IF NOT EXISTS "IX_UserDiscoveryPicks_CatalogItemId" ON "UserDiscoveryPicks" ("CatalogItemId");
            CREATE INDEX IF NOT EXISTS "IX_UserDiscoveryPicks_ExpiresAt" ON "UserDiscoveryPicks" ("ExpiresAt");
            CREATE INDEX IF NOT EXISTS "IX_UserDiscoveryPicks_Kind_Country" ON "UserDiscoveryPicks" ("Kind", "Country");
            CREATE TABLE IF NOT EXISTS "UserItemMemories" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UserItemMemories" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "TmdbId" INTEGER NOT NULL,
                "MediaType" INTEGER NOT NULL,
                "TimesRecommended" INTEGER NOT NULL,
                "LastRecommendedAt" TEXT NULL,
                "Impressions" INTEGER NOT NULL,
                "Engagements" INTEGER NOT NULL,
                "LastEngagedAt" TEXT NULL,
                "InterestScore" REAL NOT NULL,
                "CooldownUntil" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserItemMemories_UserId_TmdbId_MediaType" ON "UserItemMemories" ("UserId", "TmdbId", "MediaType");
            CREATE INDEX IF NOT EXISTS "IX_UserItemMemories_UpdatedAt" ON "UserItemMemories" ("UpdatedAt");
            CREATE TABLE IF NOT EXISTS "DiscoveryRuns" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_DiscoveryRuns" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "DurationMs" INTEGER NOT NULL,
                "Generated" INTEGER NOT NULL,
                "FilteredOut" INTEGER NOT NULL,
                "FilterReasonsJson" TEXT NULL,
                "Scored" INTEGER NOT NULL,
                "DiversityReordered" INTEGER NOT NULL,
                "BelowThreshold" INTEGER NOT NULL,
                "Selected" INTEGER NOT NULL,
                "Imported" INTEGER NOT NULL,
                "PerSourceJson" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_DiscoveryRuns_UserId_StartedAt" ON "DiscoveryRuns" ("UserId", "StartedAt");
            CREATE INDEX IF NOT EXISTS "IX_DiscoveryRuns_StartedAt" ON "DiscoveryRuns" ("StartedAt");
            DELETE FROM "CatalogItems"
            WHERE "JellyfinItemId" IS NULL
              AND "Availability" = 1
              AND ("TmdbId" IS NULL OR "TmdbId" NOT IN (SELECT "TmdbId" FROM "MediaRequests"))
              AND "Id" NOT IN (SELECT "CatalogItemId" FROM "BehaviorEvents" WHERE "CatalogItemId" IS NOT NULL)
              AND "Id" NOT IN (SELECT "CatalogItemId" FROM "UserDiscoveryPicks");
            """),
        // v9 — the trailer state-machine table (trailer redesign, milestone 2). One row per title,
        // upserted as its trailer moves through the TrailerState lifecycle.
        (9,
            """
            CREATE TABLE IF NOT EXISTS "TrailerAssets" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TrailerAssets" PRIMARY KEY AUTOINCREMENT,
                "TmdbId" INTEGER NOT NULL,
                "MediaType" INTEGER NOT NULL,
                "State" INTEGER NOT NULL,
                "FilePath" TEXT NULL,
                "FileBytes" INTEGER NULL,
                "FailureReason" TEXT NULL,
                "FailureCount" INTEGER NOT NULL,
                "ReadyAt" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TrailerAssets_TmdbId_MediaType" ON "TrailerAssets" ("TmdbId", "MediaType");
            CREATE INDEX IF NOT EXISTS "IX_TrailerAssets_State" ON "TrailerAssets" ("State");
            """),
        // v12 — persisted content vectors. Until this table existed the vector index lived only in
        // memory, so every restart re-embedded the whole catalog; the provider/model/hash stamp is
        // what makes a stored vector safe to reuse rather than merely present.
        (12,
            """
            CREATE TABLE IF NOT EXISTS "CatalogItemVectors" (
                "CatalogItemId" INTEGER NOT NULL CONSTRAINT "PK_CatalogItemVectors" PRIMARY KEY,
                "Provider" TEXT NOT NULL,
                "ModelId" TEXT NOT NULL,
                "Dimensions" INTEGER NOT NULL,
                "DocumentHash" TEXT NOT NULL,
                "Vector" BLOB NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_CatalogItemVectors_Provider_ModelId" ON "CatalogItemVectors" ("Provider", "ModelId");
            """),
    };

    /// <summary>
    /// Idempotent column additions (schema v3) — TMDB artwork/trailer for requestable items. SQLite's
    /// <c>ALTER TABLE ADD COLUMN</c> is not itself re-runnable, so each is applied only when
    /// <c>PRAGMA table_info</c> shows it missing; a fresh DB (where EnsureCreated already added them
    /// from the entity) skips every one.
    /// </summary>
    private static readonly IReadOnlyList<(string Table, string Column, string Type)> ColumnAdditions = new[]
    {
        // v3 — TMDB artwork/trailer for requestable items.
        ("CatalogItems", "PosterImageUrl", "TEXT"),
        ("CatalogItems", "BackdropImageUrl", "TEXT"),
        ("CatalogItems", "TrailerUrl", "TEXT"),
        // v4 — aggregated Wholphin community rating (F7).
        ("CatalogItems", "WholphinRating", "REAL"),
        ("CatalogItems", "WholphinVotes", "INTEGER"),
        // v5 — TMDB watch-provider brand tag (studio/provider card badge).
        ("CatalogItems", "ProviderBrandId", "INTEGER"),
        ("CatalogItems", "ProviderBrandName", "TEXT"),
        ("CatalogItems", "ProvidersSyncedAt", "TEXT"),
        // v6 — LLM-generated content advisories (player content-warning overlay).
        ("CatalogItems", "ContentWarningsJson", "TEXT"),
        ("CatalogItems", "ContentWarningsSyncedAt", "TEXT"),
        // v8 — richer taste signals: original language + collection/franchise (affinity + diversity).
        ("CatalogItems", "OriginalLanguage", "TEXT"),
        ("CatalogItems", "CollectionName", "TEXT"),
        // v10 — trailer cache management + metadata (nullable; the TrailerAssets table is created in v9).
        ("TrailerAssets", "AccessCount", "INTEGER"),
        ("TrailerAssets", "LastAccessedAt", "TEXT"),
        ("TrailerAssets", "Pinned", "INTEGER"),
        ("TrailerAssets", "DurationMs", "INTEGER"),
        ("TrailerAssets", "PreviewStartMs", "INTEGER"),
        // v11 — multi-provider metadata: clear logo, external critic scores, per-field provenance,
        // and the round-robin cursor the aggregating enricher pages through.
        ("CatalogItems", "LogoImageUrl", "TEXT"),
        ("CatalogItems", "RatingsJson", "TEXT"),
        ("CatalogItems", "MetadataSourcesJson", "TEXT"),
        ("CatalogItems", "MetadataSyncedAt", "TEXT"),
    };

    private readonly IWholphinDbContextFactory _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInitializer"/> class.
    /// </summary>
    public DatabaseInitializer(IWholphinDbContextFactory factory, ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = _factory.Create();
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

        await ApplyMigrationsAsync(db, cancellationToken).ConfigureAwait(false);

        var count = await db.CatalogItems.CountAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Orca Engine database ready ({Count} catalog items).", count);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ApplyMigrationsAsync(WholphinDbContext db, CancellationToken ct)
    {
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
            }

            int current;
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "PRAGMA user_version;";
                current = Convert.ToInt32(await read.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
            }

            if (current >= SchemaVersion)
            {
                return;
            }

            foreach (var (version, sql) in Migrations)
            {
                if (version > current)
                {
                    await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
                    _logger.LogInformation("Orca Engine: applied schema migration v{Version}.", version);
                }
            }

            // Idempotent column additions (safe to run whenever current < SchemaVersion).
            await EnsureColumnsAsync(connection, ct).ConfigureAwait(false);

            // PRAGMA can't be parameterized; SchemaVersion is a trusted compile-time constant.
            await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version = {SchemaVersion};", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A migration failure must not take down the plugin; affected features degrade instead.
            _logger.LogError(ex, "Orca Engine: schema migration failed; continuing with the existing schema.");
        }
    }

    private async Task EnsureColumnsAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
    {
        foreach (var byTable in ColumnAdditions.GroupBy(c => c.Table))
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var info = connection.CreateCommand())
            {
                // PRAGMA can't be parameterized; the table name comes from a trusted compile-time constant.
                info.CommandText = $"PRAGMA table_info(\"{byTable.Key}\");";
                await using var reader = await info.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk → name is ordinal 1.
                    existing.Add(reader.GetString(1));
                }
            }

            foreach (var (table, column, type) in byTable)
            {
                if (existing.Contains(column))
                {
                    continue;
                }

                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type} NULL;";
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Orca Engine: added column {Table}.{Column}.", table, column);
            }
        }
    }
}
