using System.Net;
using System.Net.Http;
using Wholphin.Engine.Diagnostics;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// The outbound-HTTP timing handler. The load-bearing test here is the credential one: TMDB puts
/// the API key in the query string, and everything this handler records is served over HTTP and
/// downloadable from the dashboard.
/// </summary>
public class OrcaMetricsHandlerTests
{
    private const string TmdbUrl = "https://api.themoviedb.org/3/movie/157336?api_key=SUPERSECRET&language=en";

    [Fact]
    public async Task NeverRecordsTheQueryString()
    {
        var metrics = new RecordingMetrics();
        await SendAsync(metrics, TmdbUrl, HttpStatusCode.OK);

        var recorded = string.Join("\n", metrics.Records.Select(r => $"{r.Key} {r.Data}"))
            + string.Join("\n", metrics.Counters.Keys);

        Assert.DoesNotContain("SUPERSECRET", recorded, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", recorded, StringComparison.Ordinal);
        Assert.Contains("/3/movie/157336", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_CountsOkAndTimesIt_KeyedOnHostOnly()
    {
        var metrics = new RecordingMetrics();
        await SendAsync(metrics, TmdbUrl, HttpStatusCode.OK);

        Assert.Equal(1, metrics.Counters["http.api.themoviedb.org.ok"]);
        Assert.Equal(1, metrics.Counters["http.api.themoviedb.org.count"]);
        Assert.True(metrics.Counters.ContainsKey("http.api.themoviedb.org.total_ms"));
        Assert.False(metrics.Counters.ContainsKey("http.api.themoviedb.org.error"));
    }

    [Fact]
    public async Task HttpErrorStatus_CountsAsError_ButStillReturns()
    {
        var metrics = new RecordingMetrics();
        var response = await SendAsync(metrics, TmdbUrl, HttpStatusCode.TooManyRequests);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, metrics.Counters["http.api.themoviedb.org.error"]);
        Assert.False(metrics.Records[0].Ok);
        Assert.Contains("429", metrics.Records[0].Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throw_IsRecordedWithTheException_AndRethrown()
    {
        var metrics = new RecordingMetrics();
        var handler = new OrcaMetricsHandler(metrics)
        {
            InnerHandler = new StubHandler(_ => throw new HttpRequestException("no route to host")),
        };
        using var invoker = new HttpMessageInvoker(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, TmdbUrl), CancellationToken.None));

        Assert.Equal(1, metrics.Counters["http.api.themoviedb.org.error"]);
        Assert.Contains("no route to host", metrics.Records[0].Exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_IsNotRecordedAsAFailure()
    {
        var metrics = new RecordingMetrics();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = new OrcaMetricsHandler(metrics)
        {
            InnerHandler = new StubHandler(ct => throw new OperationCanceledException(ct)),
        };
        using var invoker = new HttpMessageInvoker(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, TmdbUrl), cts.Token));

        // An abandoned call is neither the integration's fault nor a real duration.
        Assert.Empty(metrics.Records);
        Assert.Empty(metrics.Counters);
    }

    private static async Task<HttpResponseMessage> SendAsync(IEngineMetrics metrics, string url, HttpStatusCode status)
    {
        var handler = new OrcaMetricsHandler(metrics)
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(status)),
        };
        using var invoker = new HttpMessageInvoker(handler);
        return await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, HttpResponseMessage> _respond;

        public StubHandler(Func<CancellationToken, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(cancellationToken));
    }

    private sealed class RecordingMetrics : IEngineMetrics
    {
        public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);

        public List<(string Key, bool Ok, string? Data, Exception? Exception)> Records { get; } = new();

        public void Increment(string key, long by = 1) =>
            Counters[key] = Counters.TryGetValue(key, out var current) ? current + by : by;

        public void Record(string key, long elapsedMs, bool ok = true, string? data = null, Exception? exception = null)
        {
            Records.Add((key, ok, data, exception));
            Increment($"{key}.count");
            Increment($"{key}.total_ms", elapsedMs);
        }

        public IReadOnlyDictionary<string, long> Snapshot() => Counters;

        public void Reset() => Counters.Clear();
    }
}
