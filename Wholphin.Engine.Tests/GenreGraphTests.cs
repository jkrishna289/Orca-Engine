using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Recommendation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the genre co-occurrence graph.</summary>
public class GenreGraphTests
{
    [Fact]
    public void Build_WeightsByConditionalCooccurrence()
    {
        var items = new List<IReadOnlyList<string>>
        {
            new[] { "Crime", "Thriller" },
            new[] { "Crime", "Thriller" },
            new[] { "Crime", "Mystery" },
        };

        var graph = GenreGraph.Build(items);

        Assert.True(graph.ContainsKey("Crime"));
        var links = graph["Crime"];

        // P(Thriller | Crime) = co(2) / count(3) = 0.667; ranked above Mystery = 0.333.
        var thriller = links.First(l => l.Genre == "Thriller");
        var mystery = links.First(l => l.Genre == "Mystery");
        Assert.Equal(2.0 / 3.0, thriller.Weight, 3);
        Assert.Equal(1.0 / 3.0, mystery.Weight, 3);
        Assert.Equal("Thriller", links[0].Genre); // sorted strongest-first
    }
}
