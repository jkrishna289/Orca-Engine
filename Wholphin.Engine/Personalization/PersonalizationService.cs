using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Stores;

namespace Wholphin.Engine.Personalization;

/// <summary>
/// Computes affinity vectors from the behavior log: each signal's weight is decayed by age,
/// spread across the item's features, accumulated per dimension, then normalized. A confidence
/// score scales with data density so sparse profiles fall back to global priors downstream.
/// </summary>
public class PersonalizationService : IPersonalizationService
{
    /// <summary>Exponential time-decay half-life in days (a 90-day-old signal counts half).</summary>
    public const double HalfLifeDays = 90.0;

    /// <summary>Event count at which confidence reaches 1.0.</summary>
    public const int ConfidenceFullAt = 30;

    /// <summary>Event count at which a single daypart's confidence reaches 1.0 (dayparts hold ~¼ the data).</summary>
    public const int DaypartConfidenceFullAt = 12;

    /// <summary>Maximum weight the current daypart's taste can pull the blended vector (scaled by daypart confidence).</summary>
    public const double DaypartBlendWeight = 0.30;

    /// <summary>How much the in-session taste pulls the effective vector (Feature D: 30% session / 70% long-term).</summary>
    public const double SessionBlendWeight = 0.30;

    /// <summary>How recent activity must be to count as "this session".</summary>
    public static readonly TimeSpan SessionWindow = TimeSpan.FromHours(3);

    /// <summary>Number of distinct recent items that seed the session vector.</summary>
    private const int SessionItemCount = 10;

    /// <summary>How many recent events to scan to find the distinct session items.</summary>
    private const int SessionEventScan = 50;

    private readonly IWholphinDbContextFactory _factory;
    private readonly IProfileStore _profiles;
    private readonly ILogger<PersonalizationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonalizationService"/> class.
    /// </summary>
    /// <param name="factory">Database context factory.</param>
    /// <param name="profiles">Profile store.</param>
    /// <param name="logger">Logger.</param>
    public PersonalizationService(
        IWholphinDbContextFactory factory,
        IProfileStore profiles,
        ILogger<PersonalizationService> logger)
    {
        _factory = factory;
        _profiles = profiles;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AffinityVector> RecomputeAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = _factory.Create();

        var events = await db.BehaviorEvents.AsNoTracking()
            .Where(e => e.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var vector = new AffinityVector
        {
            GeneratedAt = DateTime.UtcNow,
            EventCount = events.Count,
            Confidence = Math.Min(1.0, events.Count / (double)ConfidenceFullAt)
        };

        if (events.Count > 0)
        {
            var itemIds = events
                .Where(e => e.JellyfinItemId.HasValue)
                .Select(e => e.JellyfinItemId!.Value)
                .Distinct()
                .ToList();

            var items = await db.CatalogItems.AsNoTracking()
                .Where(c => c.JellyfinItemId.HasValue && itemIds.Contains(c.JellyfinItemId.Value))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var byJellyfinId = items
                .GroupBy(c => c.JellyfinItemId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            // Request affinity: events on not-yet-available titles (e.g. RequestCreated) carry a
            // CatalogItemId instead of a Jellyfin id, so resolve those by catalog row id too.
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

            var genre = new Dictionary<string, double>();
            var studio = new Dictionary<string, double>();
            var tag = new Dictionary<string, double>();
            var decade = new Dictionary<string, double>();
            var mediaType = new Dictionary<string, double>();
            var person = new Dictionary<string, double>();

            // Context-aware (Feature C): accumulate a parallel sub-vector per daypart.
            var dayparts = new Dictionary<string, DimensionAccumulator>();

            var now = DateTime.UtcNow;
            foreach (var e in events)
            {
                var weight = SignalWeights.For(e.EventType, e.Value);
                if (weight == 0)
                {
                    continue;
                }

                var item = ResolveItem(e, byJellyfinId, byCatalogId);
                if (item is null)
                {
                    continue;
                }

                var ageDays = Math.Max(0, (now - e.Timestamp).TotalDays);
                var effective = weight * Math.Pow(0.5, ageDays / HalfLifeDays);

                foreach (var g in ParseArray(item.GenresJson))
                {
                    Accumulate(genre, g, effective);
                }

                foreach (var s in ParseArray(item.StudiosJson))
                {
                    Accumulate(studio, s, effective);
                }

                foreach (var t in ParseArray(item.TagsJson))
                {
                    Accumulate(tag, t, effective);
                }

                foreach (var p in ParseArray(item.PeopleJson))
                {
                    Accumulate(person, p, effective);
                }

                if (item.ProductionYear is { } year)
                {
                    Accumulate(decade, ((year / 10) * 10).ToString(CultureInfo.InvariantCulture), effective);
                }

                Accumulate(mediaType, item.MediaType.ToString(), effective);

                // Fold the same contribution into this event's daypart sub-vector (if its hour is known).
                if (HourOf(e.ContextJson) is { } hour)
                {
                    var bucket = Daypart.Of(hour);
                    if (!dayparts.TryGetValue(bucket, out var acc))
                    {
                        acc = new DimensionAccumulator();
                        dayparts[bucket] = acc;
                    }

                    acc.Add(item, effective);
                }
            }

            // Feature E: implicit negative signals. Group the funnel events per item and penalize
            // titles the user repeatedly sees but never engages with (or keeps focusing yet never
            // plays); the penalty is decayed by recency and folded into the same dimensions.
            ApplyNegativeEngagement(events, byJellyfinId, byCatalogId, now, genre, studio, tag, decade, mediaType, person);

            vector.Genre = Normalize(genre);
            vector.Studio = Normalize(studio);
            vector.Tag = Normalize(tag);
            vector.Decade = Normalize(decade);
            vector.MediaType = Normalize(mediaType);
            vector.Person = Normalize(person);

            foreach (var (bucket, acc) in dayparts)
            {
                vector.Dayparts[bucket] = acc.Build(now);
            }
        }

        var profile = await _profiles.GetAsync(userId, ct).ConfigureAwait(false)
            ?? new UserProfile { UserId = userId };
        profile.AffinityJson = JsonSerializer.Serialize(vector);
        profile.EventCount = events.Count;
        profile.UpdatedAt = vector.GeneratedAt;
        await _profiles.SaveAsync(profile, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Orca Engine: recomputed affinity for {UserId} from {Count} events (confidence {Confidence:F2}).",
            userId,
            events.Count,
            vector.Confidence);

        return vector;
    }

    /// <inheritdoc />
    public async Task<AffinityVector> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _profiles.GetAsync(userId, ct).ConfigureAwait(false);
        if (profile?.AffinityJson is { Length: > 0 } json)
        {
            try
            {
                return JsonSerializer.Deserialize<AffinityVector>(json) ?? new AffinityVector();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Orca Engine: corrupt affinity JSON for {UserId}; returning empty.", userId);
            }
        }

        return new AffinityVector();
    }

    /// <inheritdoc />
    public async Task<AffinityVector> GetContextualAsync(Guid userId, int? hourUtc, CancellationToken ct = default)
    {
        var baseVector = await GetAsync(userId, ct).ConfigureAwait(false);
        if (baseVector.Dayparts.Count == 0)
        {
            return baseVector;
        }

        var bucket = Daypart.Of(hourUtc ?? DateTime.UtcNow.Hour);
        if (!baseVector.Dayparts.TryGetValue(bucket, out var dp) || dp is null)
        {
            return baseVector;
        }

        var weight = Math.Clamp(DaypartBlendWeight * dp.Confidence, 0, 1);
        if (weight <= 0)
        {
            return baseVector;
        }

        // Blend the all-time taste with the current daypart's taste; keep the overall confidence
        // (data density) so the recommender's personal-vs-prior gate behaves as before.
        return new AffinityVector
        {
            Genre = Blend(baseVector.Genre, dp.Genre, weight),
            Studio = Blend(baseVector.Studio, dp.Studio, weight),
            Tag = Blend(baseVector.Tag, dp.Tag, weight),
            Decade = Blend(baseVector.Decade, dp.Decade, weight),
            MediaType = Blend(baseVector.MediaType, dp.MediaType, weight),
            Person = Blend(baseVector.Person, dp.Person, weight),
            Confidence = baseVector.Confidence,
            EventCount = baseVector.EventCount,
            GeneratedAt = baseVector.GeneratedAt,
        };
    }

    /// <inheritdoc />
    public async Task<AffinityVector> GetEffectiveAsync(Guid userId, int? hourUtc, CancellationToken ct = default)
    {
        // Start from the daypart-adjusted long-term taste, then pull toward the current session.
        var contextual = await GetContextualAsync(userId, hourUtc, ct).ConfigureAwait(false);

        await using var db = _factory.Create();
        var session = await BuildSessionVectorAsync(db, userId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return contextual;
        }

        return new AffinityVector
        {
            Genre = Blend(contextual.Genre, session.Genre, SessionBlendWeight),
            Studio = Blend(contextual.Studio, session.Studio, SessionBlendWeight),
            Tag = Blend(contextual.Tag, session.Tag, SessionBlendWeight),
            Decade = Blend(contextual.Decade, session.Decade, SessionBlendWeight),
            MediaType = Blend(contextual.MediaType, session.MediaType, SessionBlendWeight),
            Person = Blend(contextual.Person, session.Person, SessionBlendWeight),
            Confidence = contextual.Confidence,
            EventCount = contextual.EventCount,
            GeneratedAt = contextual.GeneratedAt,
        };
    }

    /// <summary>
    /// Builds a transient session vector from the user's last few engaged items within the session
    /// window, or null when there's no recent activity. No storage/expiration is needed — the time
    /// window is the expiry, and it's recomputed on demand.
    /// </summary>
    private async Task<AffinityVector?> BuildSessionVectorAsync(WholphinDbContext db, Guid userId, CancellationToken ct)
    {
        var since = DateTime.UtcNow - SessionWindow;
        var recent = await db.BehaviorEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.Timestamp >= since && e.JellyfinItemId != null)
            .OrderByDescending(e => e.Timestamp)
            .Take(SessionEventScan)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (recent.Count == 0)
        {
            return null;
        }

        // Most-recent-first, distinct items, capped at the session size.
        var seen = new HashSet<Guid>();
        var picks = new List<BehaviorEvent>();
        foreach (var e in recent)
        {
            if (seen.Add(e.JellyfinItemId!.Value))
            {
                picks.Add(e);
                if (picks.Count >= SessionItemCount)
                {
                    break;
                }
            }
        }

        var ids = picks.Select(p => p.JellyfinItemId!.Value).ToList();
        var items = await db.CatalogItems.AsNoTracking()
            .Where(c => c.JellyfinItemId != null && ids.Contains(c.JellyfinItemId.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byId = items
            .GroupBy(c => c.JellyfinItemId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var acc = new DimensionAccumulator();
        var contributed = false;
        foreach (var e in picks)
        {
            var weight = SignalWeights.For(e.EventType, e.Value);
            if (weight < 0)
            {
                // A disliked/abandoned item shouldn't shape "what you're into right now".
                continue;
            }

            if (!byId.TryGetValue(e.JellyfinItemId!.Value, out var item))
            {
                continue;
            }

            // Any recent engagement counts as current interest (floor at 1.0).
            acc.Add(item, Math.Max(weight, 1.0));
            contributed = true;
        }

        return contributed ? acc.Build(DateTime.UtcNow) : null;
    }

    private static CatalogItem? ResolveItem(
        BehaviorEvent e,
        Dictionary<Guid, CatalogItem> byJellyfinId,
        Dictionary<long, CatalogItem> byCatalogId)
    {
        if (e.JellyfinItemId.HasValue)
        {
            return byJellyfinId.GetValueOrDefault(e.JellyfinItemId.Value);
        }

        if (e.CatalogItemId.HasValue)
        {
            return byCatalogId.GetValueOrDefault(e.CatalogItemId.Value);
        }

        return null;
    }

    private static void ApplyNegativeEngagement(
        List<BehaviorEvent> events,
        Dictionary<Guid, CatalogItem> byJellyfinId,
        Dictionary<long, CatalogItem> byCatalogId,
        DateTime now,
        Dictionary<string, double> genre,
        Dictionary<string, double> studio,
        Dictionary<string, double> tag,
        Dictionary<string, double> decade,
        Dictionary<string, double> mediaType,
        Dictionary<string, double> person)
    {
        var byItem = new Dictionary<long, EngagementCounts>();
        foreach (var e in events)
        {
            var item = ResolveItem(e, byJellyfinId, byCatalogId);
            if (item is null)
            {
                continue;
            }

            if (!byItem.TryGetValue(item.Id, out var c))
            {
                c = new EngagementCounts { Item = item };
                byItem[item.Id] = c;
            }

            switch (e.EventType)
            {
                case BehaviorEventType.CardImpression: c.Shown++; break;
                case BehaviorEventType.CardFocused: c.Focused++; break;
                case BehaviorEventType.CardClicked: c.Clicked++; break;
                case BehaviorEventType.PlaybackStarted:
                case BehaviorEventType.PlaybackCompleted:
                case BehaviorEventType.MarkedPlayed: c.Played++; break;
                default: continue; // not a funnel event
            }

            if (e.Timestamp > c.LatestTs)
            {
                c.LatestTs = e.Timestamp;
            }
        }

        foreach (var c in byItem.Values)
        {
            var penalty = NegativeSignals.Penalty(c.Shown, c.Focused, c.Clicked, c.Played);
            if (penalty <= 0)
            {
                continue;
            }

            var ageDays = Math.Max(0, (now - c.LatestTs).TotalDays);
            var effective = -penalty * Math.Pow(0.5, ageDays / HalfLifeDays);

            var item = c.Item;
            foreach (var g in ParseArray(item.GenresJson))
            {
                Accumulate(genre, g, effective);
            }

            foreach (var s in ParseArray(item.StudiosJson))
            {
                Accumulate(studio, s, effective);
            }

            foreach (var t in ParseArray(item.TagsJson))
            {
                Accumulate(tag, t, effective);
            }

            foreach (var p in ParseArray(item.PeopleJson))
            {
                Accumulate(person, p, effective);
            }

            if (item.ProductionYear is { } year)
            {
                Accumulate(decade, ((year / 10) * 10).ToString(CultureInfo.InvariantCulture), effective);
            }

            Accumulate(mediaType, item.MediaType.ToString(), effective);
        }
    }

    private static int? HourOf(string? contextJson)
    {
        if (string.IsNullOrEmpty(contextJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(contextJson);
            if (doc.RootElement.TryGetProperty("hour", out var h)
                && h.ValueKind == JsonValueKind.Number
                && h.TryGetInt32(out var hour)
                && hour is >= 0 and <= 23)
            {
                return hour;
            }
        }
        catch (JsonException)
        {
            // malformed context — no daypart attribution
        }

        return null;
    }

    private static Dictionary<string, double> Blend(Dictionary<string, double> allTime, Dictionary<string, double> daypart, double weight)
    {
        var result = new Dictionary<string, double>(allTime.Count);
        foreach (var kv in allTime)
        {
            result[kv.Key] = (1 - weight) * kv.Value;
        }

        foreach (var kv in daypart)
        {
            result[kv.Key] = result.GetValueOrDefault(kv.Key) + (weight * kv.Value);
        }

        return result;
    }

    private static void Accumulate(Dictionary<string, double> dim, string key, double value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        dim[key] = dim.TryGetValue(key, out var existing) ? existing + value : value;
    }

    private static Dictionary<string, double> Normalize(Dictionary<string, double> dim)
    {
        if (dim.Count == 0)
        {
            return dim;
        }

        var max = dim.Values.Max(Math.Abs);
        if (max <= 0)
        {
            return dim;
        }

        return dim.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / max, 4));
    }

    private static IEnumerable<string> ParseArray(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Per-item funnel tallies used to derive implicit negative signals (Feature E).</summary>
    private sealed class EngagementCounts
    {
        public CatalogItem Item { get; set; } = null!;

        public int Shown { get; set; }

        public int Focused { get; set; }

        public int Clicked { get; set; }

        public int Played { get; set; }

        public DateTime LatestTs { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// Accumulates weighted feature contributions across events, then normalizes into an
    /// <see cref="AffinityVector"/>. Builds a single daypart sub-vector.
    /// </summary>
    private sealed class DimensionAccumulator
    {
        private readonly Dictionary<string, double> _genre = new();
        private readonly Dictionary<string, double> _studio = new();
        private readonly Dictionary<string, double> _tag = new();
        private readonly Dictionary<string, double> _decade = new();
        private readonly Dictionary<string, double> _mediaType = new();
        private readonly Dictionary<string, double> _person = new();
        private int _events;

        public void Add(CatalogItem item, double effective)
        {
            _events++;
            foreach (var g in ParseArray(item.GenresJson))
            {
                Accumulate(_genre, g, effective);
            }

            foreach (var s in ParseArray(item.StudiosJson))
            {
                Accumulate(_studio, s, effective);
            }

            foreach (var t in ParseArray(item.TagsJson))
            {
                Accumulate(_tag, t, effective);
            }

            foreach (var p in ParseArray(item.PeopleJson))
            {
                Accumulate(_person, p, effective);
            }

            if (item.ProductionYear is { } year)
            {
                Accumulate(_decade, ((year / 10) * 10).ToString(CultureInfo.InvariantCulture), effective);
            }

            Accumulate(_mediaType, item.MediaType.ToString(), effective);
        }

        public AffinityVector Build(DateTime now) => new()
        {
            Genre = Normalize(_genre),
            Studio = Normalize(_studio),
            Tag = Normalize(_tag),
            Decade = Normalize(_decade),
            MediaType = Normalize(_mediaType),
            Person = Normalize(_person),
            EventCount = _events,
            Confidence = Math.Min(1.0, _events / (double)DaypartConfidenceFullAt),
            GeneratedAt = now,
        };
    }
}
