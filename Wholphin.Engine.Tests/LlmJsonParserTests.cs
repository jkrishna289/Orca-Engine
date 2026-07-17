using Wholphin.Engine.Llm;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the defensive LLM JSON parsing ladder.</summary>
public class LlmJsonParserTests
{
    private const string CleanReply =
        "{\"recommendations\":[{\"title\":\"Blade Runner 2049\",\"year\":2017,\"rationale\":\"Moody sci-fi.\",\"source_title\":\"Dune\"}]}";

    [Fact]
    public void ParsesCleanJson()
    {
        var recs = LlmJsonParser.TryParseRecommendations(CleanReply);

        Assert.NotNull(recs);
        var rec = Assert.Single(recs!);
        Assert.Equal("Blade Runner 2049", rec.Title);
        Assert.Equal(2017, rec.Year);
        Assert.Equal("Moody sci-fi.", rec.Rationale);
        Assert.Equal("Dune", rec.SourceTitle);
    }

    [Fact]
    public void ParsesFencedJson()
    {
        var recs = LlmJsonParser.TryParseRecommendations("```json\n" + CleanReply + "\n```");
        Assert.NotNull(recs);
        Assert.Single(recs!);
    }

    [Fact]
    public void ParsesProseWrappedJson()
    {
        var recs = LlmJsonParser.TryParseRecommendations("Sure! Here are my picks:\n" + CleanReply + "\nEnjoy!");
        Assert.NotNull(recs);
        Assert.Single(recs!);
    }

    [Fact]
    public void ExtractsFirstBalancedObjectWithNestedBracesAndEscapedQuotes()
    {
        var json = "{\"a\":{\"b\":\"close } brace \\\" inside\"},\"c\":1}";
        Assert.Equal(json, LlmJsonParser.ExtractJsonObject("noise " + json + " {\"second\":2}"));
    }

    [Fact]
    public void AcceptsStringYears()
    {
        var recs = LlmJsonParser.TryParseRecommendations(
            "{\"recommendations\":[{\"title\":\"Arrival\",\"year\":\"2016\",\"rationale\":\"x\"}]}");
        Assert.Equal(2016, Assert.Single(recs!).Year);
    }

    [Fact]
    public void ImplausibleYearBecomesNull()
    {
        var recs = LlmJsonParser.TryParseRecommendations(
            "{\"recommendations\":[{\"title\":\"Arrival\",\"year\":16,\"rationale\":\"x\"}]}");
        Assert.Null(Assert.Single(recs!).Year);
    }

    [Fact]
    public void DropsEntriesWithoutTitles_KeepsTheRest()
    {
        var recs = LlmJsonParser.TryParseRecommendations(
            "{\"recommendations\":[{\"year\":2000,\"rationale\":\"no title\"},{\"title\":\"Heat\",\"year\":1995,\"rationale\":\"ok\"}]}");
        Assert.Equal("Heat", Assert.Single(recs!).Title);
    }

    [Fact]
    public void MissingOptionalFieldsAreDefaulted()
    {
        var recs = LlmJsonParser.TryParseRecommendations("{\"recommendations\":[{\"title\":\"Heat\"}]}");
        var rec = Assert.Single(recs!);
        Assert.Null(rec.Year);
        Assert.Equal(string.Empty, rec.Rationale);
        Assert.Null(rec.SourceTitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I cannot help with that request.")]
    [InlineData("{\"recommendations\": \"not an array\"}")]
    [InlineData("{\"wrong_key\": []}")]
    [InlineData("{\"recommendations\":[{\"year\":2000}]}")] // no valid entries at all
    [InlineData("{ unterminated")]
    public void UnusableRepliesReturnNull(string? raw)
    {
        Assert.Null(LlmJsonParser.TryParseRecommendations(raw));
    }
}
