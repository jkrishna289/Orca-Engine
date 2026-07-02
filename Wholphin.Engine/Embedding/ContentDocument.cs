using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Embedding;

/// <summary>
/// Builds the canonical natural-language "document" for a catalog item — title, year, type, genres,
/// studios, cast/crew, tags and overview as readable text. Every <see cref="IEmbeddingProvider"/>
/// consumes this same string, so local TF-IDF and hosted embedding models see identical content.
/// </summary>
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
