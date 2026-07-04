using Wholphin.Engine.Discovery;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the country → dominant-language map.</summary>
public class CountryLanguagesTests
{
    [Theory]
    [InlineData("IN", "hi")]
    [InlineData("JP", "ja")]
    [InlineData("KR", "ko")]
    [InlineData("in", "hi")] // case-insensitive
    public void For_MapsKnownCountries(string cc, string expected)
    {
        Assert.Equal(expected, CountryLanguages.For(cc));
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("USA")] // wrong length
    public void For_UnknownOrInvalid_ReturnsNull(string? cc)
    {
        Assert.Null(CountryLanguages.For(cc));
    }
}
