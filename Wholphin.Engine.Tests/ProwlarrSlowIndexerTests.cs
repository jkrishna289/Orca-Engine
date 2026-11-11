using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Integrations.Prowlarr;
using Xunit;

namespace Wholphin.Engine.Tests;

public class ProwlarrSlowIndexerTests
{
    private const string Indexers =
        """[{"id":1,"name":"Healthy","privacy":"public"},{"id":2,"name":"Dead","privacy":"public"},{"id":3,"name":"Members","privacy":"private"}]""";

    private const string Release =
        """[{"title":"Interstellar 2014 1080p","size":1000,"seeders":50,"indexerId":1,"magnetUrl":"magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"}]""";

    [Fact]
    public async Task AnIndexerThatNeverAnswersDoesNotCostTheOthersTheirResults()
    {
        var handler = new PerIndexerHandler();
        var client = new ProwlarrClient(new StubHttpClientFactory(handler), new RecordingMetrics(), NullLogger<ProwlarrClient>.Instance)
        {
            IndexerBudget = TimeSpan.FromMilliseconds(300),
        };

        var clock = Stopwatch.StartNew();
        var found = await client.SearchAsync("http://prowlarr", "key", "Interstellar 2014", movie: true, CancellationToken.None);

        Assert.Single(found);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed} on the dead indexer");
        // The private indexer is never even asked.
        Assert.DoesNotContain(handler.Queries, q => q.Contains("indexerIds=3", StringComparison.Ordinal));
    }

    private sealed class PerIndexerHandler : HttpMessageHandler
    {
        public List<string> Queries { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Queries)
            {
                Queries.Add(uri.Query);
            }

            if (uri.AbsolutePath.EndsWith("/indexer", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Json(HttpStatusCode.OK, Indexers);
            }

            if (uri.Query.Contains("indexerIds=2", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return FakeHttpHandler.Json(HttpStatusCode.OK, Release);
        }
    }
}
