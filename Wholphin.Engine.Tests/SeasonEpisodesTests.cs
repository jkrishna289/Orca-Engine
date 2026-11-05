using System.Text.Json;
using Wholphin.Engine.Integrations.Tmdb;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The episode list a series page shows for a season that isn't in the library. A wrong number here
/// searches the wrong episode's torrents.
/// </summary>
public class SeasonEpisodesTests
{
    private const string Season = """
        {"episodes":[
          {"season_number":1,"episode_number":2,"name":"Two","overview":"","air_date":"2018-02-06","runtime":70,
           "still_path":"/b.jpg","vote_average":0,"guest_stars":[{"name":"Ann"},{"name":"Ann"},{"name":""}]},
          {"season_number":1,"episode_number":1,"name":"One","overview":"Pilot.","runtime":0,"still_path":null,"vote_average":8.1},
          {"season_number":1,"episode_number":null,"name":"Broken"}
        ]}
        """;

    [Fact]
    public void Episodes_ComeInOrder_WithEmptiesAsNull()
    {
        var eps = TmdbClient.ToEpisodes(JsonSerializer.Deserialize<TmdbSeasonDetail>(Season));

        Assert.Equal([1, 2], eps.Select(e => e.EpisodeNumber));
        Assert.Equal("Pilot.", eps[0].Overview);
        Assert.Null(eps[0].RuntimeMinutes);
        Assert.Null(eps[0].StillUrl);
        Assert.Equal(8.1, eps[0].CommunityRating);
        Assert.Null(eps[1].Overview);
        Assert.Null(eps[1].CommunityRating);
        Assert.Equal(70, eps[1].RuntimeMinutes);
        Assert.Equal("https://image.tmdb.org/t/p/w300/b.jpg", eps[1].StillUrl);
        Assert.Equal(["Ann"], eps[1].GuestStars);
    }

    [Fact]
    public void NoSeason_IsEmpty() => Assert.Empty(TmdbClient.ToEpisodes(null));
}
