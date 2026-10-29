using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Presentation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>A title being streamed sits in Continue Watching like the rest: wide, with its progress.</summary>
public class StreamResumeCardTests
{
    [Fact]
    public void AResumableTitleOutsideTheLibraryGetsAWideCard()
    {
        var streamed = new CatalogItem { TmdbId = 1, MediaType = MediaType.Movie, Title = "Streamed", Availability = AvailabilityState.Request };

        var card = new DefaultCardSelector().Select(streamed, new CardSelectionContext { RowPurpose = "continue", HasResume = true });

        Assert.Equal(CardType.BannerWide, card.Type);
    }
}
