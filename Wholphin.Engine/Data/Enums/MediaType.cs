using System.Text.Json.Serialization;

namespace Wholphin.Engine.Data.Enums;

/// <summary>
/// High-level media type for a catalog item.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MediaType
{
    /// <summary>Unknown / unmapped.</summary>
    Unknown = 0,

    /// <summary>A movie.</summary>
    Movie = 1,

    /// <summary>A TV series.</summary>
    Series = 2,

    /// <summary>A season of a series.</summary>
    Season = 3,

    /// <summary>An episode of a series.</summary>
    Episode = 4,

    /// <summary>A person (cast/crew).</summary>
    Person = 5,

    /// <summary>A collection / box set.</summary>
    Collection = 6,

    /// <summary>Any other type.</summary>
    Other = 99
}
