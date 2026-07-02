using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using MediaType = Wholphin.Engine.Data.Enums.MediaType;

namespace Wholphin.Engine.Sync;

/// <summary>
/// Maps a Jellyfin <see cref="BaseItem"/> into the engine's <see cref="CatalogItem"/> projection.
/// </summary>
public static class CatalogMapper
{
    private const long TicksPerMinute = 600_000_000L; // 10,000,000 ticks/sec * 60

    private const int MaxActors = 8;
    private const int MaxPeople = 15;

    /// <summary>Projects a library item into a catalog row (available = WatchNow).</summary>
    /// <param name="item">The Jellyfin item.</param>
    /// <param name="people">Optional cast/crew for the item (populates people affinity).</param>
    public static CatalogItem Map(BaseItem item, IReadOnlyList<PersonInfo>? people = null)
    {
        var providerIds = item.ProviderIds;
        return new CatalogItem
        {
            JellyfinItemId = item.Id,
            TmdbId = ParseInt(providerIds, "Tmdb"),
            ImdbId = Get(providerIds, "Imdb"),
            TvdbId = ParseInt(providerIds, "Tvdb"),
            MediaType = MapKind(item.GetBaseItemKind()),
            Availability = AvailabilityState.WatchNow,
            Title = item.Name ?? string.Empty,
            OriginalTitle = item.OriginalTitle,
            ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate,
            RuntimeMinutes = item.RunTimeTicks is { } ticks ? (int)(ticks / TicksPerMinute) : null,
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            CriticRating = item.CriticRating,
            Overview = item.Overview,
            GenresJson = ToJson(item.Genres),
            StudiosJson = ToJson(item.Studios),
            TagsJson = ToJson(item.Tags),
            PeopleJson = PeopleToJson(people),
            DateAdded = item.DateCreated,
            LastSyncedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Serializes the relevant cast/crew as role-prefixed names ("Actor:Tom Cruise",
    /// "Director:Christopher Nolan"), capping actors so a big cast doesn't dominate. The role prefix
    /// lets the Person affinity dimension distinguish actors/directors/writers with one key space.
    /// </summary>
    private static string? PeopleToJson(IReadOnlyList<PersonInfo>? people)
    {
        if (people is null || people.Count == 0)
        {
            return null;
        }

        var names = new List<string>();
        var actors = 0;
        foreach (var p in people)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                continue;
            }

            var role = p.Type switch
            {
                PersonKind.Director => "Director",
                PersonKind.Writer => "Writer",
                PersonKind.Actor => "Actor",
                _ => null,
            };

            if (role is null)
            {
                continue;
            }

            if (role == "Actor")
            {
                if (actors >= MaxActors)
                {
                    continue;
                }

                actors++;
            }

            names.Add($"{role}:{p.Name.Trim()}");
            if (names.Count >= MaxPeople)
            {
                break;
            }
        }

        return names.Count == 0 ? null : JsonSerializer.Serialize(names);
    }

    /// <summary>Maps a Jellyfin item kind to the engine's media type.</summary>
    public static MediaType MapKind(BaseItemKind kind) => kind switch
    {
        BaseItemKind.Movie => MediaType.Movie,
        BaseItemKind.Series => MediaType.Series,
        BaseItemKind.Season => MediaType.Season,
        BaseItemKind.Episode => MediaType.Episode,
        BaseItemKind.Person => MediaType.Person,
        BaseItemKind.BoxSet => MediaType.Collection,
        _ => MediaType.Other,
    };

    private static string? Get(Dictionary<string, string>? ids, string key)
    {
        if (ids is null)
        {
            return null;
        }

        foreach (var kv in ids)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
            {
                return kv.Value;
            }
        }

        return null;
    }

    private static int? ParseInt(Dictionary<string, string>? ids, string key)
    {
        var raw = Get(ids, key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? ToJson(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(values);
    }
}
