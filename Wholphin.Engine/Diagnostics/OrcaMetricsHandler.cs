using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Diagnostics;

/// <summary>
/// Times every outbound call the engine makes and records its outcome, so an integration that has
/// gone slow or started failing is visible without reading the server log.
/// </summary>
/// <remarks>
/// <para>
/// Attached to a NAMED client (<see cref="ClientName"/>) rather than to everything. The tempting
/// alternative — <c>ConfigureAll&lt;HttpClientFactoryOptions&gt;</c> — is the only way to catch a
/// bare <c>CreateClient()</c>, but it would instrument every HttpClient in the Jellyfin process
/// (image downloads, Live TV, DLNA) and put a bug in this class in the path of the whole server's
/// outbound HTTP. A named client keeps the blast radius at exactly the engine's own calls.
/// </para>
/// <para>
/// Counters are keyed on HOST only. Keying on path would grow the counter dictionary without bound
/// — and <c>Snapshot()</c> copies that dictionary on every dashboard poll.
/// </para>
/// </remarks>
public sealed class OrcaMetricsHandler : DelegatingHandler
{
    /// <summary>The named <see cref="IHttpClientFactory"/> client that carries this handler.</summary>
    public const string ClientName = "OrcaEngine";

    private readonly IEngineMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrcaMetricsHandler"/> class.
    /// </summary>
    /// <param name="metrics">The metric sink.</param>
    public OrcaMetricsHandler(IEngineMetrics metrics) => _metrics = metrics;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "unknown";
        var key = $"http.{host}";

        // Path only, NEVER the query string: TMDB carries the API key there, and these events are
        // exported and downloaded from the dashboard.
        var where = $"{request.Method} {request.RequestUri?.AbsolutePath}";
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var ok = response.IsSuccessStatusCode;
            _metrics.Increment(ok ? $"{key}.ok" : $"{key}.error");
            _metrics.Record(key, ElapsedMs(startedAt), ok, $"{where} → {(int)response.StatusCode}");
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up (row budget blown, client disconnected). Timing an abandoned call
            // would report a truncated duration, and it isn't the integration's failure.
            throw;
        }
        catch (Exception ex)
        {
            // Everything else IS the integration's problem — including the HttpClient timeout,
            // which arrives here as a cancellation the caller did not ask for.
            _metrics.Increment($"{key}.error");
            _metrics.Record(key, ElapsedMs(startedAt), ok: false, data: where, exception: ex);
            throw;
        }
    }

    private static long ElapsedMs(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
