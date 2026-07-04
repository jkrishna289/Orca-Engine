using System.Collections.Generic;
using System.Text.Json;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Discovery;
using Wholphin.Engine.Integrations.Jellyseerr;

namespace Wholphin.Engine.Tests;

/// <summary>Small factories for building test fixtures without touching the database.</summary>
internal static class TestData
{
    public static CatalogItem Item(
        long id = 1,
        MediaType mediaType = MediaType.Movie,
        int? year = 2020,
        float? rating = 7.0f,
        string[]? genres = null,
        string[]? tags = null,
        string[]? studios = null,
        string[]? people = null) => new()
    {
        Id = id,
        MediaType = mediaType,
        ProductionYear = year,
        CommunityRating = rating,
        GenresJson = Json(genres),
        TagsJson = Json(tags),
        StudiosJson = Json(studios),
        PeopleJson = Json(people),
    };

    public static ScoredCandidate Scored(int tmdbId, string title, double finalScore, params string[] genres)
    {
        var candidate = new DiscoveryCandidate
        {
            Result = new DiscoverResult
            {
                TmdbId = tmdbId,
                MediaType = MediaType.Movie,
                Title = title,
                Genres = new List<string>(genres),
            },
            Kind = DiscoveryPickKind.TasteMatch,
        };
        return new ScoredCandidate(candidate, new DiscoveryScore { Final = finalScore });
    }

    private static string? Json(string[]? values)
        => values is { Length: > 0 } ? JsonSerializer.Serialize(values) : null;
}
