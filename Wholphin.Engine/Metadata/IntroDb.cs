using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wholphin.Engine.Metadata;

/// <summary>When one part of a title runs, in milliseconds; no end means it runs to the end of the file.</summary>
public sealed record SkipTimes(
    [property: JsonPropertyName("StartMs")] long StartMs,
    [property: JsonPropertyName("EndMs")] long? EndMs);

/// <summary>The skippable parts of a movie or episode. Any of them may be unknown.</summary>
public sealed record SkipSegments(
    [property: JsonPropertyName("Intro")] SkipTimes? Intro,
    [property: JsonPropertyName("Recap")] SkipTimes? Recap,
    [property: JsonPropertyName("Outro")] SkipTimes? Outro)
{
    /// <summary>Nothing known.</summary>
    public static readonly SkipSegments None = new(null, null, null);

    /// <summary>Gets a value indicating whether anything is known.</summary>
    [JsonIgnore]
    public bool IsEmpty => Intro is null && Recap is null && Outro is null;
}

/// <summary>
/// Skip times from IntroDB (introdb.app), a community database keyed on IMDb ids. Reads are anonymous
/// and rate limited, so callers cache what comes back. A streamed file isn't in the library, so
/// Jellyfin's own segment detection never sees it; this is where its intro times come from instead.
/// </summary>
public static class IntroDb
{
    private const string Endpoint = "https://api.introdb.app/segments";

    /// <summary>The lookup URL for an episode (season and episode given) or a movie.</summary>
    public static string Url(string imdbId, int? season, int? episode) =>
        season is { } s && episode is { } e
            ? string.Create(CultureInfo.InvariantCulture, $"{Endpoint}?imdb_id={Uri.EscapeDataString(imdbId)}&season={s}&episode={e}")
            : $"{Endpoint}?imdb_id={Uri.EscapeDataString(imdbId)}&is_movie=true";

    /// <summary>Reads a <c>/segments</c> response. Unknown shapes read as nothing known.</summary>
    public static SkipSegments Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object
            ? new SkipSegments(Read(root, "intro"), Read(root, "recap"), Read(root, "outro"))
            : SkipSegments.None;
    }

    private static SkipTimes? Read(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var seg) || seg.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var start = Ms(seg, "start_ms", "start_sec");
        var end = Ms(seg, "end_ms", "end_sec");
        return start is { } s && s >= 0 && (end is null || end > s) ? new SkipTimes(s, end) : null;
    }

    private static long? Ms(JsonElement seg, string ms, string sec) =>
        seg.TryGetProperty(ms, out var m) && m.ValueKind == JsonValueKind.Number ? (long)m.GetDouble()
        : seg.TryGetProperty(sec, out var s) && s.ValueKind == JsonValueKind.Number ? (long)(s.GetDouble() * 1000)
        : null;
}
