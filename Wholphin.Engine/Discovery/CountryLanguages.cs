using System;
using System.Collections.Generic;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// A small static map from ISO-3166-1 country codes to the dominant local ISO-639-1 language,
/// used to add local-language flavor to the per-country discovery pull and as the weak
/// country boost in personal scoring. Deliberately coarse — countries without an entry simply
/// skip the language leg.
/// </summary>
public static class CountryLanguages
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IN"] = "hi",
        ["JP"] = "ja",
        ["KR"] = "ko",
        ["FR"] = "fr",
        ["DE"] = "de",
        ["ES"] = "es",
        ["IT"] = "it",
        ["BR"] = "pt",
        ["PT"] = "pt",
        ["CN"] = "zh",
        ["TW"] = "zh",
        ["RU"] = "ru",
        ["MX"] = "es",
        ["AR"] = "es",
        ["TR"] = "tr",
        ["TH"] = "th",
        ["ID"] = "id",
        ["VN"] = "vi",
        ["US"] = "en",
        ["GB"] = "en",
        ["AU"] = "en",
        ["CA"] = "en",
    };

    /// <summary>Resolves the dominant local language for a country, or null when unmapped.</summary>
    /// <param name="countryCode">The ISO-3166-1 country code.</param>
    /// <returns>The ISO-639-1 language code, or null.</returns>
    public static string? For(string? countryCode)
        => countryCode is { Length: 2 } cc && Map.TryGetValue(cc, out var lang) ? lang : null;
}
