using Wholphin.Engine.Discovery.Sources;
using Wholphin.Engine.Personalization;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Which languages earn their own TMDB pull. This decides what is allowed into the candidate pool
/// at all — the scoring stage can only rank titles that were fetched, so a language missing here is
/// a language the user can never be recommended from.
/// </summary>
public class TasteDiscoverLanguageTests
{
    private static UserTasteProfile Profile(params string[] languages) =>
        new() { TopLanguages = new List<string>(languages) };

    [Fact]
    public void ANonEnglishViewerGetsTheirLanguage()
    {
        Assert.Equal(new[] { "hi" }, TasteDiscoverSource.PullLanguages(Profile("hi")));
    }

    [Fact]
    public void EnglishIsSkippedBecauseTheGenreLegAlreadyCoversIt()
    {
        Assert.Empty(TasteDiscoverSource.PullLanguages(Profile("en")));
        Assert.Equal(new[] { "hi" }, TasteDiscoverSource.PullLanguages(Profile("en", "hi")));
        Assert.Equal(new[] { "hi" }, TasteDiscoverSource.PullLanguages(Profile("EN", "hi")));
    }

    [Fact]
    public void CapsAtTwoSoThePullDoesNotDiluteIntoNoise()
    {
        var languages = TasteDiscoverSource.PullLanguages(Profile("hi", "ta", "ja", "ko"));

        Assert.Equal(TasteDiscoverSource.MaxLanguages, languages.Count);
        Assert.Equal(new[] { "hi", "ta" }, languages);
    }

    [Fact]
    public void AProfileWithNoLanguagesAsksForNoExtraLegs()
    {
        Assert.Empty(TasteDiscoverSource.PullLanguages(Profile()));
        Assert.Empty(TasteDiscoverSource.PullLanguages(Profile("", "   ")));
    }
}
