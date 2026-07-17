using System.Collections.Generic;
using System.Linq;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Llm;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The curation-mode contract of <see cref="LlmReRanker.Parse"/>: selection drops what the model
/// dropped (the row IS the picks), reorder mode re-appends everything, and fenced replies still parse.
/// </summary>
public class LlmCurationParseTests
{
    private static List<CatalogItem> Pool(int count)
        => Enumerable.Range(0, count)
            .Select(i => new CatalogItem { Id = i + 1, Title = $"Title {i}" })
            .ToList();

    [Fact]
    public void Selection_DropsOmittedCandidates()
    {
        var pool = Pool(6);
        var result = LlmReRanker.Parse("{\"order\":[4,1]}", pool, pool, selection: true, count: 10);

        Assert.NotNull(result);
        Assert.Equal(new long[] { 5, 2 }, result!.Items.Select(i => i.Id));
    }

    [Fact]
    public void Selection_CapsAtCount()
    {
        var pool = Pool(6);
        var result = LlmReRanker.Parse("{\"order\":[0,1,2,3,4,5]}", pool, pool, selection: true, count: 3);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Items.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, result.Items.Select(i => i.Id));
    }

    [Fact]
    public void ReorderMode_ReappendsOmitted()
    {
        var pool = Pool(4);
        var result = LlmReRanker.Parse("{\"order\":[2]}", pool, pool, selection: false);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Items.Count);
        Assert.Equal(3, result.Items[0].Id);
    }

    [Fact]
    public void FencedReply_StillParses()
    {
        var pool = Pool(3);
        var fenced = "```json\n{\"order\":[1,0],\"reasons\":{\"1\":\"fits the mood\"}}\n```";
        var result = LlmReRanker.Parse(fenced, pool, pool, selection: true, count: 3);

        Assert.NotNull(result);
        Assert.Equal(new long[] { 2, 1 }, result!.Items.Select(i => i.Id));
        Assert.Equal("fits the mood", result.Reasons[2]);
    }

    [Fact]
    public void CandidateNameAsRowTitle_IsRejected()
    {
        var pool = Pool(4);
        var result = LlmReRanker.Parse("{\"order\":[0,1],\"title\":\"Title 1\"}", pool, pool, selection: true, count: 4);

        Assert.NotNull(result);
        Assert.Null(result!.RowTitle);
    }

    [Fact]
    public void GarbageReply_ReturnsNull()
    {
        var pool = Pool(4);
        Assert.Null(LlmReRanker.Parse("sorry, I cannot help with that", pool, pool, selection: true, count: 4));
        Assert.Null(LlmReRanker.Parse("{\"order\":[]}", pool, pool, selection: true, count: 4));
    }
}
