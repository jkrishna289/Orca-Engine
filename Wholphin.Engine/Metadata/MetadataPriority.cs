using System;
using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Configuration;

namespace Wholphin.Engine.Metadata;

/// <summary>
/// The admin's per-field provider order. Priority is field-specific because provider strengths
/// genuinely differ: Fanart has the best artwork, OMDb has the only real critic scores, TMDB has the
/// best core data. A single global order could not express that.
/// </summary>
/// <remarks>
/// Same comma-token idiom as the trailer source order: removing a token disables that provider for
/// that field, reordering changes preference, and an unrecognised token is ignored — so a typo costs
/// one provider rather than breaking metadata resolution.
/// </remarks>
public sealed class MetadataPriority
{
    /// <summary>Rank given to a provider the admin did not list — after everything they did.</summary>
    private const int Unlisted = 1000;

    private readonly Dictionary<MetadataCapability, IReadOnlyList<string>> _orders;

    private MetadataPriority(Dictionary<MetadataCapability, IReadOnlyList<string>> orders) => _orders = orders;

    /// <summary>Reads the orders from configuration, falling back to the shipped defaults.</summary>
    /// <param name="config">The plugin configuration, or null.</param>
    /// <returns>The priority table.</returns>
    public static MetadataPriority FromConfig(PluginConfiguration? config) => new(new()
    {
        [MetadataCapability.Core] = Parse(config?.MetadataPriorityCore, "tmdb", "tvdb"),
        [MetadataCapability.Artwork] = Parse(config?.MetadataPriorityArtwork, "fanart", "tmdb", "tvdb"),
        [MetadataCapability.Logo] = Parse(config?.MetadataPriorityLogo, "fanart", "tvdb"),
        [MetadataCapability.Ratings] = Parse(config?.MetadataPriorityRatings, "omdb", "tmdb"),
        [MetadataCapability.Episodes] = Parse(config?.MetadataPriorityEpisodes, "tvdb", "tmdb"),
    });

    /// <summary>Splits a comma-separated order string, trimming and lowercasing each token.</summary>
    /// <param name="csv">The configured value.</param>
    /// <param name="fallback">The shipped default, used when the value is empty.</param>
    /// <returns>The ordered provider names.</returns>
    public static IReadOnlyList<string> Parse(string? csv, params string[] fallback)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return fallback;
        }

        var tokens = csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // An admin who clears the field entirely gets the default back rather than a silently dead
        // field — "no providers for posters" is never what someone means by an empty box.
        return tokens.Count > 0 ? tokens : fallback;
    }

    /// <summary>Gets the configured order for one field group.</summary>
    /// <param name="capability">The field group.</param>
    /// <returns>The ordered provider names.</returns>
    public IReadOnlyList<string> Order(MetadataCapability capability)
        => _orders.TryGetValue(capability, out var order) ? order : Array.Empty<string>();

    /// <summary>Ranks a provider for one field group; lower is better.</summary>
    /// <param name="capability">The field group.</param>
    /// <param name="provider">The provider name.</param>
    /// <returns>The 0-based rank, or a large value when unlisted.</returns>
    public int Rank(MetadataCapability capability, string provider)
    {
        var order = Order(capability);
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], provider, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return Unlisted;
    }

    /// <summary>Every provider named anywhere in the table, for capability filtering.</summary>
    /// <returns>The distinct provider names.</returns>
    public IReadOnlyCollection<string> AllNamed()
        => _orders.Values.SelectMany(o => o).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
