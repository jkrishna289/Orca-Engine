using MediaBrowser.Model.Providers;
using Wholphin.Engine.Controllers;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>The details page's title logo, picked from what Jellyfin's image providers offer.</summary>
public class LogoPickTests
{
    private static RemoteImageInfo L(string? lang, string url) => new() { Language = lang, Url = url };

    [Fact]
    public void PrefersEnglishThenLanguageNeutralThenAnything()
    {
        Assert.Equal("en", MetadataController.PickLogo(new[] { L("ja", "ja"), L(null, "none"), L("en", "en") })?.Url);
        Assert.Equal("none", MetadataController.PickLogo(new[] { L("ja", "ja"), L(null, "none") })?.Url);
        Assert.Equal("ja", MetadataController.PickLogo(new[] { L("ja", "ja") })?.Url);
        Assert.Null(MetadataController.PickLogo(System.Array.Empty<RemoteImageInfo>()));
    }
}
