using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wholphin.Engine.Streaming;

/// <summary>
/// Brings peer discovery up at server start instead of at play time.
/// </summary>
/// <remarks>
/// Everything torrent connectivity depends on is slow to establish and was previously created by the
/// first stream: the peer listener binds, the router is asked for a UPnP mapping, Local Peer Discovery
/// begins multicasting, and DHT starts looking for nodes. DHT is the worst of them — it needs minutes,
/// and it was logged as <c>Initialising</c> fifty seconds into a stream that had itself created the
/// engine moments earlier, then <c>NotReady</c> ten seconds after that. A viewer pressing play was
/// paying for all of it.
///
/// So it starts here, and the idle sweep keeps it up: MonoTorrent stops the engine again once the last
/// torrent is removed, which is precisely what evicting an idle session does.
///
/// Deliberately fire-and-forget. Warm-up talks to the network — a router that never answers UPnP, a
/// DHT bootstrap with nowhere to go — and Jellyfin's startup must not wait on any of that.
/// </remarks>
public class TorrentEngineWarmup : IHostedService
{
    private readonly ITorrentStreamService _streams;
    private readonly ILogger<TorrentEngineWarmup> _logger;

    /// <summary>Initializes a new instance of the <see cref="TorrentEngineWarmup"/> class.</summary>
    /// <param name="streams">The torrent streaming service.</param>
    /// <param name="logger">Logger.</param>
    public TorrentEngineWarmup(ITorrentStreamService streams, ILogger<TorrentEngineWarmup> logger)
    {
        _streams = streams;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _streams.WarmUpAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Streaming still works without a warm engine; it is simply slower to start.
                    _logger.LogWarning(ex, "Orca stream: warm-up at startup failed");
                }
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
