using Wholphin.Engine.Diagnostics;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The event log's whole reason to exist is that a failure is still there when someone goes looking
/// for it. These lock in the two properties that make that true: errors are retained independently
/// of routine traffic, and both payloads are bounded.
/// </summary>
public class EngineEventsTests
{
    [Fact]
    public void Errors_SurviveAFloodOfRoutineEvents()
    {
        var events = new EngineEvents();
        events.Emit("error", "tmdb", "enrich", exception: new InvalidOperationException("boom"));

        // Far more than the info ring holds — with one shared ring the error would be long gone.
        for (var i = 0; i < EngineEvents.InfoCapacity * 2; i++)
        {
            events.Emit("info", "home", "row.foryou", elapsedMs: 5);
        }

        var errors = events.Recent(errorsOnly: true);
        Assert.Single(errors);
        Assert.Equal("tmdb", errors[0].Component);
        Assert.Contains("boom", errors[0].Exception);
    }

    [Fact]
    public void InfoRing_TrimsToCapacity()
    {
        var events = new EngineEvents();
        for (var i = 0; i < EngineEvents.InfoCapacity + 250; i++)
        {
            events.Emit("info", "home", "build");
        }

        Assert.Equal(EngineEvents.InfoCapacity, events.Recent(int.MaxValue).Count);
    }

    [Fact]
    public void Since_ReturnsOnlyNewer_OldestFirst_AcrossBothRings()
    {
        var events = new EngineEvents();
        events.Emit("info", "home", "first");
        var cursor = events.LastSeq;
        events.Emit("info", "home", "second");
        events.Emit("error", "tmdb", "third", exception: new Exception("x"));
        events.Emit("info", "home", "fourth");

        var since = events.Since(cursor);

        Assert.Equal(new[] { "second", "third", "fourth" }, since.Select(e => e.Event));
        Assert.Equal(since.OrderBy(e => e.Seq), since);
    }

    [Fact]
    public void Recent_IsNewestFirst()
    {
        var events = new EngineEvents();
        events.Emit("info", "home", "old");
        events.Emit("info", "home", "new");

        Assert.Equal("new", events.Recent()[0].Event);
    }

    [Fact]
    public void Payloads_AreTruncated()
    {
        var events = new EngineEvents();
        events.Emit(
            "error",
            "llm",
            "call",
            data: new string('d', 5000),
            exception: new Exception(new string('e', 20000)));

        var entry = events.Recent(errorsOnly: true)[0];
        Assert.True(entry.Data!.Length < 1000, $"data was {entry.Data.Length} chars");
        Assert.True(entry.Exception!.Length < 5000, $"exception was {entry.Exception.Length} chars");
    }

    [Fact]
    public void EmptyComponentOrEvent_IsDropped()
    {
        var events = new EngineEvents();
        events.Emit("info", string.Empty, "build");
        events.Emit("info", "home", string.Empty);

        Assert.Empty(events.Recent());
    }
}
