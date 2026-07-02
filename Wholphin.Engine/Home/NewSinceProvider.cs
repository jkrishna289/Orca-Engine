using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Integrations.Arr;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Home;

/// <summary>
/// Default <see cref="INewSinceProvider"/>. Combines newly-added catalog rows (library/discovery) with
/// *arr "recently downloaded" episodes (mapped back to catalog series), since the user's last visit.
/// </summary>
public class NewSinceProvider : INewSinceProvider
{
    /// <summary>Roaming-settings key holding the user's last-seen timestamp (ISO-8601 UTC).</summary>
    public const string LastSeenKey = "meta.lastSeenUtc";

    /// <summary>When a user has no last-seen yet, look back this far so the row isn't empty on first run.</summary>
    private static readonly TimeSpan FirstRunWindow = TimeSpan.FromDays(14);

    /// <summary>Don't surface items older than this even if last-seen is very old (keeps the row "fresh").</summary>
    private static readonly TimeSpan MaxLookback = TimeSpan.FromDays(45);

    private readonly IWholphinDbContextFactory _factory;
    private readonly IUserSettingsStore _userSettings;
    private readonly IArrClient _arr;
    private readonly ILogger<NewSinceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewSinceProvider"/> class.
    /// </summary>
    public NewSinceProvider(IWholphinDbContextFactory factory, IUserSettingsStore userSettings, IArrClient arr, ILogger<NewSinceProvider> logger)
    {
        _factory = factory;
        _userSettings = userSettings;
        _arr = arr;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogItem>> GetAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || limit <= 0)
        {
            return Array.Empty<CatalogItem>();
        }

        var since = await ReadLastSeenAsync(userId, ct).ConfigureAwait(false);
        var floor = DateTime.UtcNow - MaxLookback;
        if (since < floor)
        {
            since = floor;
        }

        await using var db = _factory.Create();

        // Newly-added catalog rows since the cutoff.
        var added = await db.CatalogItems
            .Where(c => c.DateAdded != null && c.DateAdded > since)
            .OrderByDescending(c => c.DateAdded)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<(CatalogItem Item, DateTime Key)>();
        var seen = new HashSet<long>();
        foreach (var item in added)
        {
            if (seen.Add(item.Id))
            {
                result.Add((item, item.DateAdded ?? DateTime.UtcNow));
            }
        }

        // *arr: episodes that downloaded since the cutoff → surface the parent series if in the catalog.
        if (_arr.IsConfigured)
        {
            try
            {
                var recent = await _arr.GetCalendarAsync(since, DateTime.UtcNow, ct).ConfigureAwait(false);
                var tmdbIds = recent.Where(e => e.HasFile && e.TmdbId is > 0).Select(e => e.TmdbId!.Value).ToHashSet();
                if (tmdbIds.Count > 0)
                {
                    var matches = await db.CatalogItems
                        .Where(c => c.TmdbId != null && tmdbIds.Contains(c.TmdbId.Value))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    foreach (var item in matches)
                    {
                        if (seen.Add(item.Id))
                        {
                            // Use the most recent matching air date as the sort key.
                            var air = recent.Where(e => e.TmdbId == item.TmdbId).Max(e => e.AirDateUtc);
                            result.Add((item, air));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orca Engine: *arr recent lookup failed for New Since Away.");
            }
        }

        return result
            .OrderByDescending(x => x.Key)
            .Take(limit)
            .Select(x => x.Item)
            .ToList();
    }

    /// <inheritdoc />
    public Task MarkSeenAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        var nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        return _userSettings.SetAsync(userId, LastSeenKey, nowIso, ct);
    }

    private async Task<DateTime> ReadLastSeenAsync(Guid userId, CancellationToken ct)
    {
        var raw = await _userSettings.GetAsync(userId, LastSeenKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(raw.Trim().Trim('"'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return DateTime.UtcNow - FirstRunWindow;
    }
}
