using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Default <see cref="ITasteProfileService"/>: derives seeds from the user's positively-weighted
/// behavior (same signal weights and 90-day half-life as the affinity vector), builds the taste
/// vector as the weighted mean of the seeds' content vectors from the current snapshot (zero extra
/// embedding calls), and persists the result as one inspectable JSON file per user.
/// </summary>
public class TasteProfileService : ITasteProfileService
{
    /// <summary>Maximum seeds kept in the profile file.</summary>
    public const int MaxSeeds = 40;

    /// <summary>Affinity above which a genre/tag counts as a top feature.</summary>
    public const double TopFeatureThreshold = 0.35;

    /// <summary>Affinity below which a genre becomes a hard avoid-veto.</summary>
    public const double AvoidGenreThreshold = -0.5;

    private const int MaxTopGenres = 5;
    private const int MaxTopKeywords = 8;
    private const int MaxTopLanguages = 2;

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };

    private readonly IWholphinDbContextFactory _factory;
    private readonly IPersonalizationService _personalization;
    private readonly IContentVectorIndex _vectorIndex;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<TasteProfileService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TasteProfileService"/> class.
    /// </summary>
    /// <param name="factory">Database context factory.</param>
    /// <param name="personalization">The personalization service (top/avoid features, confidence).</param>
    /// <param name="vectorIndex">The catalog content-vector index.</param>
    /// <param name="appPaths">Jellyfin application paths (profile file directory).</param>
    /// <param name="logger">The logger.</param>
    public TasteProfileService(
        IWholphinDbContextFactory factory,
        IPersonalizationService personalization,
        IContentVectorIndex vectorIndex,
        IApplicationPaths appPaths,
        ILogger<TasteProfileService> logger)
    {
        _factory = factory;
        _personalization = personalization;
        _vectorIndex = vectorIndex;
        _appPaths = appPaths;
        _logger = logger;
    }

    private string ProfileDir => Path.Combine(_appPaths.DataPath, "wholphin-engine", "profiles");

    /// <inheritdoc />
    public async Task<UserTasteProfile?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var path = ProfilePath(userId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<UserTasteProfile>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Orca Engine: unreadable taste-profile file for {UserId}.", userId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<UserTasteProfile> RebuildAsync(Guid userId, CancellationToken ct = default)
    {
        var affinity = await _personalization.GetAsync(userId, ct).ConfigureAwait(false);
        var profile = new UserTasteProfile
        {
            UserId = userId,
            GeneratedAt = DateTime.UtcNow,
            Confidence = affinity.Confidence,
            EventCount = affinity.EventCount,
            TopGenres = TopFeatures(affinity.Genre, MaxTopGenres),
            TopKeywords = TopFeatures(affinity.Tag, MaxTopKeywords),
            TopLanguages = TopFeatures(affinity.Language, MaxTopLanguages),
            AvoidGenres = affinity.Genre
                .Where(kv => kv.Value < AvoidGenreThreshold)
                .OrderBy(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList(),
        };

        profile.Seeds = await BuildSeedsAsync(userId, ct).ConfigureAwait(false);

        // The vector is a cache over the seeds: their weighted-mean content vector inside the
        // current catalog snapshot. Seeds are already indexed, so this costs no embedding calls.
        var snapshot = await _vectorIndex.GetAsync(ct).ConfigureAwait(false);
        profile.Provider = snapshot.ProviderName;
        var vector = VectorInSnapshotSpace(profile, snapshot);
        profile.DenseVector = vector.DenseValues?.ToArray();

        await PersistAsync(profile, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Orca Engine: rebuilt taste profile for {UserId} ({Seeds} seeds, confidence {Confidence:F2}).",
            userId,
            profile.Seeds.Count,
            profile.Confidence);
        return profile;
    }

    /// <inheritdoc />
    public ContentVector VectorInSnapshotSpace(UserTasteProfile profile, ContentVectorSnapshot snapshot)
    {
        if (profile.Seeds.Count == 0)
        {
            return ContentVector.Empty;
        }

        var parts = profile.Seeds
            .Select(s => (snapshot.VectorFor(s.CatalogItemId), s.Weight))
            .ToList();
        return ContentVector.WeightedMean(parts);
    }

    /// <summary>
    /// Derives the seed list: per resolved item, the decayed net signal weight over all of the
    /// user's events; only net-positive items qualify (a title the user abandoned or disliked must
    /// never seed a pull).
    /// </summary>
    private async Task<List<TasteSeed>> BuildSeedsAsync(Guid userId, CancellationToken ct)
    {
        await using var db = _factory.Create();
        var events = await db.BehaviorEvents.AsNoTracking()
            .Where(e => e.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (events.Count == 0)
        {
            return new List<TasteSeed>();
        }

        var jellyfinIds = events
            .Where(e => e.JellyfinItemId.HasValue)
            .Select(e => e.JellyfinItemId!.Value)
            .Distinct()
            .ToList();
        var byJellyfinId = (await db.CatalogItems.AsNoTracking()
                .Where(c => c.JellyfinItemId.HasValue && jellyfinIds.Contains(c.JellyfinItemId.Value))
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(c => c.JellyfinItemId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var catalogIds = events
            .Where(e => !e.JellyfinItemId.HasValue && e.CatalogItemId.HasValue)
            .Select(e => e.CatalogItemId!.Value)
            .Distinct()
            .ToList();
        var byCatalogId = catalogIds.Count == 0
            ? new Dictionary<long, CatalogItem>()
            : (await db.CatalogItems.AsNoTracking()
                    .Where(c => catalogIds.Contains(c.Id))
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToDictionary(c => c.Id, c => c);

        var now = DateTime.UtcNow;
        var tallies = new Dictionary<long, (CatalogItem Item, double Weight, DateTime LastAt)>();
        foreach (var e in events)
        {
            var weight = SignalWeights.For(e.EventType, e.Value);
            if (weight == 0)
            {
                continue;
            }

            var item = e.JellyfinItemId.HasValue
                ? byJellyfinId.GetValueOrDefault(e.JellyfinItemId.Value)
                : e.CatalogItemId.HasValue ? byCatalogId.GetValueOrDefault(e.CatalogItemId.Value) : null;
            if (item is null)
            {
                continue;
            }

            var ageDays = Math.Max(0, (now - e.Timestamp).TotalDays);
            var effective = weight * Math.Pow(0.5, ageDays / PersonalizationService.HalfLifeDays);
            var current = tallies.GetValueOrDefault(item.Id, (item, 0.0, DateTime.MinValue));
            tallies[item.Id] = (item, current.Item2 + effective, e.Timestamp > current.Item3 ? e.Timestamp : current.Item3);
        }

        return tallies.Values
            .Where(t => t.Weight > 0)
            .OrderByDescending(t => t.Weight)
            .Take(MaxSeeds)
            .Select(t => new TasteSeed
            {
                CatalogItemId = t.Item.Id,
                TmdbId = t.Item.TmdbId,
                MediaType = t.Item.MediaType,
                Title = t.Item.Title ?? string.Empty,
                Weight = Math.Round(t.Weight, 4),
                LastEventAt = t.LastAt,
            })
            .ToList();
    }

    private async Task PersistAsync(UserTasteProfile profile, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(ProfileDir);
            var path = ProfilePath(profile.UserId);
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, profile, FileJson, ct).ConfigureAwait(false);
            }

            // Atomic replace so a concurrent reader never sees a half-written file.
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Orca Engine: failed to persist taste-profile file for {UserId}.", profile.UserId);
        }
    }

    private string ProfilePath(Guid userId) => Path.Combine(ProfileDir, userId.ToString("N") + ".json");

    private static List<string> TopFeatures(Dictionary<string, double> dimension, int max) => dimension
        .Where(kv => kv.Value > TopFeatureThreshold)
        .OrderByDescending(kv => kv.Value)
        .Take(max)
        .Select(kv => kv.Key)
        .ToList();
}
