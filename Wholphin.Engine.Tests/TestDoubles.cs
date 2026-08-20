using System.Net;
using System.Net.Http;
using System.Text;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Tests;

/// <summary>
/// A canned HTTP transport. The engine's providers all resolve an <see cref="HttpClient"/> from
/// <see cref="IHttpClientFactory"/>, so pairing this with <see cref="StubHttpClientFactory"/> covers
/// the whole provider surface without a mocking package.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;

    /// <summary>Responds to every request with the same status and body.</summary>
    public FakeHttpHandler(HttpStatusCode status, string body)
        : this((_, _) => Json(status, body))
    {
    }

    /// <summary>Responds based on the cancellation token only (enough for throw/cancel cases).</summary>
    public FakeHttpHandler(Func<CancellationToken, HttpResponseMessage> respond)
        : this((_, ct) => respond(ct))
    {
    }

    /// <summary>Responds based on the request, so a test can vary by URL.</summary>
    public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>How many requests reached the transport — the in-flight-coalescing assertion.</summary>
    public int Calls;

    /// <summary>The absolute paths requested. Never the query string: provider URLs carry API keys.</summary>
    public List<string> Paths { get; } = new();

    /// <summary>Builds a JSON response.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>A transport that always throws — the "provider unavailable" case.</summary>
    public static FakeHttpHandler Throwing(Exception ex) => new(_ => throw ex);

    /// <summary>A transport that answers 429 with a Retry-After.</summary>
    public static FakeHttpHandler RateLimited(int retryAfterSeconds)
        => new((_, _) =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
            return response;
        });

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        lock (Paths)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
        }

        return Task.FromResult(_respond(request, cancellationToken));
    }
}

/// <summary>Hands every named client the same fake transport.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false)
    {
        BaseAddress = null,
    };
}

/// <summary>An <see cref="IEngineMetrics"/> that remembers everything, so tests can assert on it.</summary>
internal sealed class RecordingMetrics : IEngineMetrics
{
    public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);

    public List<(string Key, bool Ok, string? Data, Exception? Exception)> Records { get; } = new();

    public void Increment(string key, long by = 1)
    {
        lock (Counters)
        {
            Counters[key] = Counters.TryGetValue(key, out var current) ? current + by : by;
        }
    }

    public void Record(string key, long elapsedMs, bool ok = true, string? data = null, Exception? exception = null)
    {
        lock (Records)
        {
            Records.Add((key, ok, data, exception));
        }

        Increment($"{key}.count");
        Increment($"{key}.total_ms", elapsedMs);
    }

    public IReadOnlyDictionary<string, long> Snapshot() => Counters;

    public void Reset() => Counters.Clear();
}
