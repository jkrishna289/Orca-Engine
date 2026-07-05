using System;
using System.Diagnostics;
using System.Globalization;
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
/// Concurrency and single-flight are owned by <see cref="ITrailerQueue"/>; this service just executes one
/// produce job at a time (as invoked by a queue worker) and records each step in the trailer state machine.
/// </summary>
public class TrailerService : ITrailerService
{
    private const int DownloadTimeoutMs = 240_000;
    private const int TranscodeTimeoutMs = 240_000;
    private const int ProbeTimeoutMs = 15_000;

    /// <summary>Fallback preview start when no duration metadata is available (matches the client default).</summary>
    private const int DefaultPreviewStartMs = 3_000;

    private readonly IApplicationPaths _appPaths;
    private readonly ITmdbClient _tmdb;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ITrailerStateStore _state;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<TrailerService> _logger;

    private bool? _available;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerService"/> class.
    /// </summary>
    public TrailerService(
        IApplicationPaths appPaths,
        ITmdbClient tmdb,
        IWholphinDbContextFactory factory,
        ITrailerStateStore state,
        IEngineMetrics metrics,
        ILogger<TrailerService> logger)
    {
        _appPaths = appPaths;
        _tmdb = tmdb;
        _factory = factory;
        _state = state;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _available ??= ProbeBinaries();

    private string CacheDir => Path.Combine(_appPaths.DataPath, "wholphin-engine", "trailers");

    /// <inheritdoc />
    public string? GetCachedPath(int tmdbId, MediaType mediaType)
    {
        if (tmdbId <= 0 || mediaType is not (MediaType.Movie or MediaType.Series))
        {
            return null;
        }

        // ".full" distinguishes full-length trailers from the legacy 60s clips ("{id}.mp4").
        var outPath = Path.Combine(CacheDir, $"{tmdbId}.full.mp4");
        return File.Exists(outPath) && new FileInfo(outPath).Length > 0 ? outPath : null;
    }

    /// <inheritdoc />
    public async Task<TrailerState> ProcessAsync(int tmdbId, MediaType mediaType, CancellationToken ct = default)
    {
        if (tmdbId <= 0 || mediaType is not (MediaType.Movie or MediaType.Series))
        {
            return TrailerState.FailedPermanent;
        }

        // Already cached (warmed by an earlier run) — reconcile the state and stop.
        var cached = GetCachedPath(tmdbId, mediaType);
        if (cached is not null)
        {
            await _state.MarkReadyAsync(tmdbId, mediaType, cached, new FileInfo(cached).Length, ct).ConfigureAwait(false);
            return TrailerState.Ready;
        }

        if (!IsAvailable)
        {
            return TrailerState.FailedTemporary;
        }

        Directory.CreateDirectory(CacheDir);
        var outPath = Path.Combine(CacheDir, $"{tmdbId}.full.mp4");
        var tmp = Path.Combine(CacheDir, $"{tmdbId}.src");

        try
        {
            await _state.SetStateAsync(tmdbId, mediaType, TrailerState.Discovering, ct).ConfigureAwait(false);
            var youtubeUrl = await ResolveTrailerUrlAsync(tmdbId, mediaType, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(youtubeUrl))
            {
                // No obtainable trailer for this title — permanent, so the client stops asking.
                _metrics.Increment("trailer.resolve.none");
                await _state.SetStateAsync(tmdbId, mediaType, TrailerState.FailedPermanent, ct, "no trailer url").ConfigureAwait(false);
                return TrailerState.FailedPermanent;
            }

            // 1. yt-dlp: best stream capped at 480p (full trailers are watched, so "worst" looked
            // too rough), falling back down the format ladder when a capped mp4 isn't offered.
            await _state.SetStateAsync(tmdbId, mediaType, TrailerState.Downloading, ct).ConfigureAwait(false);
            var dl = await RunAsync("yt-dlp",
                $"-f \"best[height<=480][ext=mp4]/best[height<=480]/worst[ext=mp4]/worst\" --no-playlist --no-warnings --no-progress -o \"{tmp}\" \"{youtubeUrl}\"",
                DownloadTimeoutMs, ct).ConfigureAwait(false);
            if (!dl || !File.Exists(tmp))
            {
                _metrics.Increment("trailer.download.error");
                await _state.SetStateAsync(tmdbId, mediaType, TrailerState.FailedTemporary, ct, "download failed").ConfigureAwait(false);
                return TrailerState.FailedTemporary;
            }

            // 2. ffmpeg: FULL length (no time cap — users watch the whole trailer), downscaled to
            // 480p at a low bitrate for fast LAN streaming.
            await _state.SetStateAsync(tmdbId, mediaType, TrailerState.Transcoding, ct).ConfigureAwait(false);
            var tx = await RunAsync("ffmpeg",
                $"-y -i \"{tmp}\" -vf scale=-2:480 -b:v 700k -maxrate 900k -bufsize 1200k " +
                $"-c:v libx264 -preset veryfast -c:a aac -b:a 96k -movflags +faststart \"{outPath}\"",
                TranscodeTimeoutMs, ct).ConfigureAwait(false);
            if (!tx || !File.Exists(outPath))
            {
                _metrics.Increment("trailer.transcode.error");
                await _state.SetStateAsync(tmdbId, mediaType, TrailerState.FailedTemporary, ct, "transcode failed").ConfigureAwait(false);
                return TrailerState.FailedTemporary;
            }

            // Drop the superseded 60s clip so the cache doesn't hold both variants.
            TryDelete(Path.Combine(CacheDir, $"{tmdbId}.mp4"));

            // Phase 14: probe duration (ffprobe; fail-soft) and derive a smart preview-start offset so
            // the client skips studio idents proportionally instead of always cutting a flat 3s.
            var durationMs = await ProbeDurationMsAsync(outPath, ct).ConfigureAwait(false);
            var previewStartMs = ComputePreviewStart(durationMs);

            _metrics.Increment("trailer.cache.ok");
            await _state.MarkReadyAsync(tmdbId, mediaType, outPath, new FileInfo(outPath).Length, ct, durationMs, previewStartMs).ConfigureAwait(false);
            return TrailerState.Ready;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics.Increment("trailer.cache.error");
            _logger.LogWarning(ex, "Orca Engine: trailer cache failed for tmdb {TmdbId}.", tmdbId);
            await _state.SetStateAsync(tmdbId, mediaType, TrailerState.FailedTemporary, ct, ex.GetType().Name).ConfigureAwait(false);
            return TrailerState.FailedTemporary;
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

    /// <summary>Runs a process and returns its stdout (or null on failure/timeout/missing binary).</summary>
    private async Task<string?> RunCaptureAsync(string fileName, string args, int timeoutMs, CancellationToken ct)
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
            var stdout = p.StandardOutput.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(p);
                return null;
            }

            return p.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
        }
        catch (Exception ex)
        {
            // ffprobe not installed / spawn error — fail soft; the caller falls back to the default offset.
            _logger.LogDebug(ex, "Orca Engine: capture process {File} failed.", fileName);
            return null;
        }
    }

    /// <summary>Probes the transcoded file's duration via ffprobe (ms), or null when unavailable.</summary>
    private async Task<int?> ProbeDurationMsAsync(string filePath, CancellationToken ct)
    {
        var output = await RunCaptureAsync(
            "ffprobe",
            $"-v quiet -show_entries format=duration -of csv=p=0 \"{filePath}\"",
            ProbeTimeoutMs, ct).ConfigureAwait(false);
        if (double.TryParse(output?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return (int)(seconds * 1000);
        }

        return null;
    }

    /// <summary>
    /// Smart preview start (Phase 14): skip ~4% of the trailer (studio idents/slates scale a little with
    /// length), bounded to 1.5–6s, and never more than 15% of a short clip. Falls back to the flat 3s
    /// default when duration is unknown.
    /// </summary>
    private static int ComputePreviewStart(int? durationMs)
    {
        if (durationMs is not { } d || d <= 0)
        {
            return DefaultPreviewStartMs;
        }

        var proportional = Math.Clamp((int)(d * 0.04), 1_500, 6_000);
        var maxSkip = (int)(d * 0.15);
        return Math.Min(proportional, Math.Max(0, maxSkip));
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
