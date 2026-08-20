using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Builds the canonical natural-language "document" for a catalog item — title, year, type,
/// language, genres, studios, cast/crew, tags and overview as readable text. Every
/// <see cref="IEmbeddingProvider"/> consumes this same string, so local TF-IDF and hosted embedding
/// models see identical content.
/// </summary>
/// <remarks>
/// Language earns its place here because without it the document is effectively
/// origin-blind: TMDB overviews are written in English whatever the film, so a Hindi title and an
/// American one in the same genre produced near-identical vectors, and a viewer whose taste is
/// almost entirely non-English got matched on genre alone.
/// </remarks>
public static class ContentDocument
{
    /// <summary>Builds the text document for a catalog item.</summary>
    /// <param name="item">The catalog item.</param>
    /// <returns>A single descriptive string (never null; may be sparse for thin metadata).</returns>
    public static string Of(CatalogItem item)
    {
        var sb = new StringBuilder();

        var title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled" : item.Title.Trim();
        sb.Append(title);
        if (item.ProductionYear is { } year)
        {
            sb.Append(" (").Append(year.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        sb.Append(". ").Append(item.MediaType).Append('.');

        AppendLanguage(sb, item.OriginalLanguage);
        AppendList(sb, "Genres", CatalogFeatures.Parse(item.GenresJson), stripRolePrefix: false, max: 8);
        AppendList(sb, "Studios", CatalogFeatures.Parse(item.StudiosJson), stripRolePrefix: false, max: 6);
        AppendList(sb, "Cast and crew", CatalogFeatures.Parse(item.PeopleJson), stripRolePrefix: true, max: 12);
        AppendList(sb, "Tags", CatalogFeatures.Parse(item.TagsJson), stripRolePrefix: false, max: 12);

        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            sb.Append(" Overview: ").Append(item.Overview.Trim());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the text document for an external (not-yet-imported) discovery result, using the
    /// exact field labels and order of the catalog overload so token distributions match when
    /// seeds and candidates are embedded in the same batch. External results only carry
    /// title/year/type/genres/overview — the missing sections are simply absent, exactly as they
    /// would be for a catalog item with thin metadata.
    /// </summary>
    /// <param name="result">The discovery result.</param>
    /// <returns>A single descriptive string (never null).</returns>
    public static string Of(Integrations.Jellyseerr.DiscoverResult result)
    {
        var sb = new StringBuilder();

        var title = string.IsNullOrWhiteSpace(result.Title) ? "Untitled" : result.Title.Trim();
        sb.Append(title);
        if (result.Year is { } year)
        {
            sb.Append(" (").Append(year.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        sb.Append(". ").Append(result.MediaType).Append('.');

        AppendLanguage(sb, result.OriginalLanguage);
        AppendList(sb, "Genres", result.Genres, stripRolePrefix: false, max: 8);

        if (!string.IsNullOrWhiteSpace(result.Overview))
        {
            sb.Append(" Overview: ").Append(result.Overview.Trim());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends the original language as both its ISO code and its English name ("hi, Hindi").
    /// </summary>
    /// <remarks>
    /// Both, deliberately. The code is a stable token TF-IDF can key on even where the runtime has
    /// no ICU data; the name is what a neural embedder and the LLM re-ranker actually understand.
    /// </remarks>
    private static void AppendLanguage(StringBuilder sb, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var trimmed = code.Trim();
        sb.Append(" Language: ").Append(trimmed);

        if (EnglishName(trimmed) is { } name)
        {
            sb.Append(", ").Append(name);
        }

        sb.Append('.');
    }

    // Globalization-invariant runtimes answer "Invariant Language" for everything; that is noise,
    // not a language, so the code stands alone there.
    private static string? EnglishName(string code)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).EnglishName;
            return name.Contains("Invariant", StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> values, bool stripRolePrefix, int max)
    {
        if (values.Count == 0)
        {
            return;
        }

        sb.Append(' ').Append(label).Append(": ");
        var count = 0;
        for (var i = 0; i < values.Count && count < max; i++)
        {
            var value = stripRolePrefix ? StripRole(values[i]) : values[i].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (count > 0)
            {
                sb.Append(", ");
            }

            sb.Append(value);
            count++;
        }

        sb.Append('.');
    }

    // People are stored role-prefixed ("Director:Christopher Nolan"); the document shows the name.
    private static string StripRole(string value)
    {
        var idx = value.IndexOf(':');
        return (idx >= 0 && idx < value.Length - 1 ? value[(idx + 1)..] : value).Trim();
    }
}
