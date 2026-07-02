using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Settings;

/// <summary>
/// EF Core / SQLite implementation of <see cref="IUserSettingsStore"/> over the
/// <see cref="UserSetting"/> entity (composite key: user id + setting key).
/// </summary>
public class UserSettingsStore : IUserSettingsStore
{
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSettingsStore"/> class.
    /// </summary>
    /// <param name="factory">The database context factory.</param>
    public UserSettingsStore(IWholphinDbContextFactory factory) => _factory = factory;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        var rows = await db.UserSettings.AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var map = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            map[row.Key] = row.ValueJson;
        }

        return map;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(Guid userId, string key, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        return await db.UserSettings.AsNoTracking()
            .Where(s => s.UserId == userId && s.Key == key)
            .Select(s => s.ValueJson)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetAsync(Guid userId, string key, string valueJson, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        var existing = await db.UserSettings
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Key == key, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.UserSettings.Add(new UserSetting
            {
                UserId = userId,
                Key = key,
                ValueJson = valueJson,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.ValueJson = valueJson;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid userId, string key, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        await db.UserSettings
            .Where(s => s.UserId == userId && s.Key == key)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
