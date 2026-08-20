using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Controllers;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Recommendation;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The "Test provider" probe. Its value is that it is a POSITIVE check — every other embedding
/// signal only fires on failure, so a quiet dashboard cannot distinguish "working" from "never
/// tried". The probe has to be able to fail a provider that answers successfully with junk.
/// </summary>
public class EmbeddingProbeTests
{
    [Fact]
    public async Task AWorkingProviderIsReportedHealthy()
    {
        var result = await Probe(new ProbeProvider("cloud"));

        Assert.True(result.GetProperty("Ok").GetBoolean());
        Assert.Equal("healthy", result.GetProperty("Outcome").GetString());
        Assert.Equal("dense", result.GetProperty("Kind").GetString());
        Assert.True(result.GetProperty("RelatedScore").GetDouble() > result.GetProperty("UnrelatedScore").GetDouble());
    }

    [Fact]
    public async Task AProviderReturningConstantVectorsIsFlaggedSuspect()
    {
        // The failure a connectivity check cannot catch: HTTP 200, well-formed response, correct
        // count — and vectors that carry no meaning, so every title looks identical to every other.
        var result = await Probe(new ProbeProvider("cloud") { Constant = true });

        Assert.True(result.GetProperty("Ok").GetBoolean());
        Assert.Equal("suspect", result.GetProperty("Outcome").GetString());
        Assert.Contains("no higher", result.GetProperty("Message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderWithNoKeyIsReportedWithoutBeingCalled()
    {
        var provider = new ProbeProvider("cloud") { IsConfigured = false };

        var result = await Probe(provider);

        Assert.False(result.GetProperty("Ok").GetBoolean());
        Assert.Equal("not-configured", result.GetProperty("Outcome").GetString());
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task AProviderThatReturnsNothingIsReportedAsFailed()
    {
        var result = await Probe(new ProbeProvider("cloud") { ReturnsNull = true });

        Assert.False(result.GetProperty("Ok").GetBoolean());
        Assert.Equal("no-vectors", result.GetProperty("Outcome").GetString());
    }

    [Fact]
    public async Task AProviderThatThrowsIsReportedRatherThanBubblingUp()
    {
        var result = await Probe(new ProbeProvider("cloud") { Throws = true });

        Assert.False(result.GetProperty("Ok").GetBoolean());
        Assert.Equal("threw", result.GetProperty("Outcome").GetString());
    }

    [Fact]
    public async Task AnUnknownProviderNameIsReportedRatherThan404()
    {
        var result = await Probe(new ProbeProvider("cloud"), request: "nope");

        Assert.False(result.GetProperty("Ok").GetBoolean());
        Assert.Equal("not-registered", result.GetProperty("Outcome").GetString());
    }

    /// <summary>A throwaway database; the probe never touches it, but the store needs somewhere to point.</summary>
    private static SqliteConnection NewMemoryDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new TestDbFactory(connection).Create();
        db.Database.EnsureCreated();
        return connection;
    }

    private static async Task<JsonElement> Probe(IEmbeddingProvider provider, string? request = null)
    {
        var controller = new EmbeddingController(
            new[] { provider },
            new ScriptedEmbeddings(count => EmbeddingRun.Empty("cloud")),
            new NullVectorIndex(),
            new VectorStore(new TestDbFactory(NewMemoryDb())),
            new RecordingMetrics(),
            new RecordingAlerts());

        var action = await controller.Test(request ?? provider.Name, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action);

        // The payload is an anonymous type, which is internal to the engine assembly — `dynamic`
        // cannot bind to it from here, so it goes through the same serializer the endpoint uses.
        return JsonSerializer.SerializeToDocument(ok.Value!).RootElement;
    }
}

/// <summary>A provider whose probe behaviour each test dictates.</summary>
internal sealed class ProbeProvider : IEmbeddingProvider
{
    public ProbeProvider(string name) => Name = name;

    public string Name { get; }

    public bool IsConfigured { get; set; } = true;

    public string ModelId => "probe-model";

    public int MaxBatchSize => 96;

    /// <summary>Returns the same vector for every input — the "answers 200 with junk" case.</summary>
    public bool Constant { get; set; }

    public bool ReturnsNull { get; set; }

    public bool Throws { get; set; }

    public int Calls { get; private set; }

    public Task<IReadOnlyList<ContentVector>?> EmbedAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        Calls++;

        if (Throws)
        {
            throw new InvalidOperationException("transport exploded");
        }

        if (ReturnsNull)
        {
            return Task.FromResult<IReadOnlyList<ContentVector>?>(null);
        }

        // Probe 0 and 1 are near-identical; probe 2 is unrelated. A meaningful model places 0 and 1
        // close together, so the fake mirrors that rather than returning arbitrary numbers.
        var vectors = documents
            .Select((_, i) => Constant
                ? ContentVector.Dense(new[] { 1f, 0f, 0f })
                : ContentVector.Dense(i switch
                {
                    0 => new[] { 1f, 0.05f, 0f },
                    1 => new[] { 0.98f, 0.10f, 0f },
                    _ => new[] { 0f, 0.05f, 1f },
                }))
            .ToList();

        return Task.FromResult<IReadOnlyList<ContentVector>?>(vectors);
    }
}

/// <summary>An index that has never been built — the state a fresh restart is in.</summary>
internal sealed class NullVectorIndex : IContentVectorIndex
{
    public ContentVectorSnapshot? Current => null;

    public Task<ContentVectorSnapshot> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(ContentVectorSnapshot.Empty("ollama"));
}
