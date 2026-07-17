using System;
using System.Collections.Generic;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Llm;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the LLM-discovery prompt builder.</summary>
public class LlmDiscoveryPromptBuilderTests
{
    private static readonly IReadOnlyList<HistoryLine> None = Array.Empty<HistoryLine>();
    private static readonly IReadOnlyList<string> NoNames = Array.Empty<string>();

    [Fact]
    public void IncludesAllSectionsWhenPopulated()
    {
        var prompt = LlmDiscoveryPromptBuilder.Build(
            MediaType.Movie,
            5,
            new[] { new HistoryLine("Dune", 2021) },
            new[] { new HistoryLine("The Martian", 2015) },
            new[] { new HistoryLine("Cats", 2019) },
            new[] { "Science Fiction" },
            new[] { "Horror" },
            new[] { "Denis Villeneuve" });

        Assert.Contains("exactly 5 movies", prompt, StringComparison.Ordinal);
        Assert.Contains("Loved: Dune (2021)", prompt, StringComparison.Ordinal);
        Assert.Contains("Watched and finished: The Martian (2015)", prompt, StringComparison.Ordinal);
        Assert.Contains("Disliked", prompt, StringComparison.Ordinal);
        Assert.Contains("Cats (2019)", prompt, StringComparison.Ordinal);
        Assert.Contains("Favorite genres: Science Fiction", prompt, StringComparison.Ordinal);
        Assert.Contains("Never recommend these genres: Horror", prompt, StringComparison.Ordinal);
        Assert.Contains("Likes work by: Denis Villeneuve", prompt, StringComparison.Ordinal);
        Assert.Contains("source_title", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OmitsEmptySections()
    {
        var prompt = LlmDiscoveryPromptBuilder.Build(
            MediaType.Movie, 5, new[] { new HistoryLine("Dune", 2021) }, None, None, NoNames, NoNames, NoNames);

        Assert.DoesNotContain("Disliked", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Watched and finished", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Favorite genres", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Likes work by", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesCallUsesSeriesNouns()
    {
        var prompt = LlmDiscoveryPromptBuilder.Build(
            MediaType.Series, 8, new[] { new HistoryLine("Dark", 2017) }, None, None, NoNames, NoNames, NoNames);

        Assert.Contains("exactly 8 TV series", prompt, StringComparison.Ordinal);
        Assert.Contains("real TV series", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("movies", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void YearlessLinesRenderWithoutParentheses()
    {
        var prompt = LlmDiscoveryPromptBuilder.Build(
            MediaType.Movie, 3, new[] { new HistoryLine("Dune", null) }, None, None, NoNames, NoNames, NoNames);
        Assert.Contains("Loved: Dune\n", prompt.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Dark S02E12", "Dark")]
    [InlineData("Dark - S02E12 - The Endless", "Dark")]
    [InlineData("Dark – S2E1", "Dark")]
    [InlineData("Dune (2021)", "Dune")]
    [InlineData("Dune", "Dune")]
    [InlineData("  Interstellar  ", "Interstellar")]
    [InlineData("1917 (2019)", "1917")]
    [InlineData("", "")]
    public void NormalizeTitle_StripsEpisodeNotationAndYearSuffixes(string raw, string expected)
    {
        Assert.Equal(expected, LlmDiscoveryPromptBuilder.NormalizeTitle(raw));
    }

    [Fact]
    public void PromptNeverContainsIdentity()
    {
        // The builder receives only titles + public metadata by construction; guard the constants too.
        Assert.Contains("anonymous viewer", LlmDiscoveryPromptBuilder.SystemPrompt, StringComparison.Ordinal);
        var prompt = LlmDiscoveryPromptBuilder.Build(
            MediaType.Movie, 5, new[] { new HistoryLine("Dune", 2021) }, None, None, NoNames, NoNames, NoNames);
        Assert.Contains("anonymized", prompt, StringComparison.Ordinal);
    }
}
