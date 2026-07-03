using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Integrations.Tmdb;

namespace Wholphin.Engine.Trailer;

/// <summary>
/// Default <see cref="ITrailerService"/>. Shells out to yt-dlp + ffmpeg (the engine's only external-binary
/// dependency) to fetch and transcode a short, low-bitrate trailer clip, cached under the engine data dir.
/// </summary>
public class TrailerService : ITrailerService
{
    private const int ClipSeconds = 60;
    private const int DownloadTimeoutMs = 120_000;
    private const int TranscodeTimeoutMs = 120_000;

    /// <summary>
    /// Hard cap per warm batch. Sized for the worker's two pools (24 WatchNow + 24 requestable) —
    /// it silently truncated larger batches when it was 15, starving the discover-row trailers.
    /// </summary>
    private const int MaxPrebuffer = 60;

    private readonly IApplicationPaths _appPaths;
    private readonly ITmdbClient _tmdb;
    private readonly IWholphinDbContextFactory _factory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<TrailerService> _logger;

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private bool? _available;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerService"/> class.
    /// </summary>
    public TrailerService(
        IApplicationPaths appPaths,
        ITmdbClient tmdb,
        IWholphinDbContextFactory factory,
        IEngineMetrics metrics,
        ILogger<TrailerService> logger)
    {
        _appPaths = appPaths;
        _tmdb = tmdb;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _available ??= ProbeBinaries();

    private string CacheDir => Path.Combine(_appPaths.DataPath, "wholphin-engine", "trailers");

    /// <inheritdoc />
    public async Task<string?> GetTrailerPathAsync(int tmdbId, MediaType mediaType, bool allowDownload, CancellationToken ct = default)
    {
        if (tmdbId <= 0 || mediaType is not (MediaType.Movie or MediaType.Series))
        {
            return null;
        }

        var outPath = Path.Combine(CacheDir, $"{tmdbId}.mp4");
        if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
        {
            return outPath;
        }

        if (!allowDownload || !IsAvailable)
        {
            return null;
        }

        var gate = _locks.GetOrAdd(tmdbId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock (another request may have just cached it).
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
            {
                return outPath;
            }

            return await DownloadAndTranscodeAsync(tmdbId, mediaType, outPath, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> PrebufferAsync(IEnumerable<(int TmdbId, MediaType MediaType)> items, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return 0;
        }

        var cached = 0;
        foreach (var (tmdbId, mediaType) in items.Take(MaxPrebuffer))
        {
            ct.ThrowIfCancellationRequested();
            var path = await GetTrailerPathAsync(tmdbId, mediaType, allowDownload: true, ct).ConfigureAwait(false);
            if (path is not null)
            {
                cached++;
            }
        }

        return cached;
    }

    private async Task<string?> DownloadAndTranscodeAsync(int tmdbId, MediaType mediaType, string outPath, CancellationToken ct)
    {
        var youtubeUrl = await ResolveTrailerUrlAsync(tmdbId, mediaType, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(youtubeUrl))
        {
            return null;
        }

        Directory.CreateDirectory(CacheDir);
        var tmp = Path.Combine(CacheDir, $"{tmdbId}.src");

        try
        {
            // 1. yt-dlp: grab the smallest mp4 (we only need a low-quality preview).
            var dl = await RunAsync("yt-dlp",
                $"-f \"worst[ext=mp4]/worst\" --no-playlist --no-warnings --no-progress -o \"{tmp}\" \"{youtubeUrl}\"",
                DownloadTimeoutMs, ct).ConfigureAwait(false);
            if (!dl || !File.Exists(tmp))
            {
                _metrics.Increment("trailer.download.error");
                return null;
            }

            // 2. ffmpeg: cap length + downscale to 480p at a low bitrate for fast streaming.
            var tx = await RunAsync("ffmpeg",
                $"-y -i \"{tmp}\" -t {ClipSeconds} -vf scale=-2:480 -b:v 700k -maxrate 900k -bufsize 1200k " +
                $"-c:v libx264 -preset veryfast -c:a aac -b:a 96k -movflags +faststart \"{outPath}\"",
                TranscodeTimeoutMs, ct).ConfigureAwait(false);
            if (!tx || !File.Exists(outPath))
            {
                _metrics.Increment("trailer.transcode.error");
                return null;
            }

            _metrics.Increment("trailer.cache.ok");
            return outPath;
        }
        catch (Exception ex)
        {
            _metrics.Increment("trailer.cache.error");
            _logger.LogWarning(ex, "Orca Engine: trailer cache failed for tmdb {TmdbId}.", tmdbId);
            return null;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private async Task<string?> ResolveTrailerUrlAsync(int tmdbId, MediaType mediaType, CancellationToken ct)
    {
        await using var db = _factory.Create();
        var stored = await db.CatalogItems
            .Where(c => c.TmdbId == tmdbId)
            .Select(c => c.TrailerUrl)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        // Not stored — ask TMDB directly (only when configured).
        if (_tmdb.IsConfigured)
        {
            var enrichment = await _tmdb.EnrichAsync(tmdbId, mediaType, ct).ConfigureAwait(false);
            return enrichment?.TrailerUrl;
        }

        return null;
    }

    private bool ProbeBinaries()
    {
        var ok = ProbeOne("yt-dlp", "--version") && ProbeOne("ffmpeg", "-version");
        _logger.LogInformation("Orca Engine: trailer binaries {State} (yt-dlp + ffmpeg).", ok ? "available" : "NOT available");
        return ok;
    }

    private bool ProbeOne(string fileName, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
            {
                return false;
            }

            if (!p.WaitForExit(5000))
            {
                TryKill(p);
                return false;
            }

            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> RunAsync(string fileName, string args, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            p.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(p);
                return false;
            }

            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: process {File} failed.", fileName);
            return false;
        }
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
