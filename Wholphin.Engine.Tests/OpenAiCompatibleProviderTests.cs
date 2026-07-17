using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Llm;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for endpoint building, legacy-config fallback, and TMDB search matching.</summary>
public class OpenAiCompatibleProviderTests
{
    [Theory]
    [InlineData(null, "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("", "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("   ", "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    [InlineData(" https://api.openai.com/v1 ", "https://api.openai.com/v1/chat/completions")]
    public void BuildEndpoint_HandlesDefaultsAndTrailingSlashes(string? baseUrl, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleLlmProvider.BuildEndpoint(baseUrl));
    }

    [Theory]
    [InlineData("new-key", "groq-key", "new-key")]  // generic field wins
    [InlineData("", "groq-key", "groq-key")]        // legacy fallback
    [InlineData(" padded ", null, "padded")]        // trimming
    [InlineData("", "", null)]                      // neither set
    [InlineData(null, null, null)]
    public void ResolveApiKey_PrefersGenericThenLegacy(string? llmKey, string? groqKey, string? expected)
    {
        Assert.Equal(expected, OpenAiCompatibleLlmProvider.ResolveApiKey(llmKey, groqKey));
    }

    [Theory]
    [InlineData("gpt-4o-mini", "llama-3.3-70b-versatile", "gpt-4o-mini")]
    [InlineData("", "custom-groq-model", "custom-groq-model")]
    [InlineData("", "", "llama-3.3-70b-versatile")] // built-in default
    public void ResolveModel_PrefersGenericThenLegacyThenDefault(string? llmModel, string? groqModel, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleLlmProvider.ResolveModel(llmModel, groqModel));
    }

    [Fact]
    public void PickSearchMatch_PrefersExactTitleWithinYearTolerance()
    {
        var results = new[]
        {
            new DiscoverResult { TmdbId = 1, Title = "Suspiria", Year = 1977 },
            new DiscoverResult { TmdbId = 2, Title = "Suspiria", Year = 2018 },
        };

        Assert.Equal(2, TmdbClient.PickSearchMatch(results, "Suspiria", 2018).TmdbId);
        Assert.Equal(1, TmdbClient.PickSearchMatch(results, "Suspiria", 1977).TmdbId);
        Assert.Equal(1, TmdbClient.PickSearchMatch(results, "Suspiria", 1978).TmdbId); // ±1 tolerance
    }

    [Fact]
    public void PickSearchMatch_FallsBackToExactTitle_ThenNearYear_ThenFirst()
    {
        var results = new[]
        {
            new DiscoverResult { TmdbId = 1, Title = "Something Else", Year = 2001 },
            new DiscoverResult { TmdbId = 2, Title = "The Thing", Year = 1982 },
        };

        // Exact title beats position even when the year is far off.
        Assert.Equal(2, TmdbClient.PickSearchMatch(results, "The Thing", 2011).TmdbId);

        // No exact title: the near-year result wins over position.
        Assert.Equal(2, TmdbClient.PickSearchMatch(results, "Unknown Title", 1983).TmdbId);

        // No exact title, no near year: first result.
        Assert.Equal(1, TmdbClient.PickSearchMatch(results, "Unknown Title", 1950).TmdbId);
        Assert.Equal(1, TmdbClient.PickSearchMatch(results, "Unknown Title", null).TmdbId);
    }
}
