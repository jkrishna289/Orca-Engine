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
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Http;
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
    // 1080p sources are several times the old 480p ones: give the DASH merge and the full-length
    // transcode room to finish on modest hardware instead of killing them mid-file.
    private const int DownloadTimeoutMs = 480_000;
    private const int TranscodeTimeoutMs = 600_000;
    private const int ProbeTimeoutMs = 15_000;

    /// <summary>Fallback preview start when no duration metadata is available (matches the client default).</summary>
    private const int DefaultPreviewStartMs = 3_000;

    /// <summary>Used when the admin has not set an order (or has cleared it to nothing).</summary>
    private const string DefaultSourceOrder = "tmdb,jellyfin,stored";

    private readonly IApplicationPaths _appPaths;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
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
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        ITmdbClient tmdb,
        IWholphinDbContextFactory factory,
        ITrailerStateStore state,
        IEngineMetrics metrics,
        ILogger<TrailerService> logger)
    {
        _appPaths = appPaths;
        _libraryManager = libraryManager;
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

        // ".hd" distinguishes the 1080p clips from the superseded 480p ".full" and legacy 60s
        // "{id}.mp4" generations — bumping the suffix is what forces old blurry files to re-produce.
        var outPath = Path.Combine(CacheDir, $"{tmdbId}.hd.mp4");
        return File.Exists(outPath) && new FileInfo(outPath).Length > 0 ? outPath : null;
    }

    /// <inheritdoc />
    public async Task<TrailerState> ProcessAsync(int tmdbId, MediaType mediaType, string? lang = null, CancellationToken ct = default)
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
        var outPath = Path.Combine(CacheDir, $"{tmdbId}.hd.mp4");
        var tmp = Path.Combine(CacheDir, $"{tmdbId}.src");
        // yt-dlp writes "{tmp}.mp4": with a DASH merge (bestvideo+bestaudio) the merger appends the
        // container extension anyway, so the template pins it for both merged and progressive picks.
        var tmpDl = tmp + ".mp4";
        // ffmpeg's in-progress output; only moved onto outPath after a successful transcode.
        var partPath = outPath + ".part.mp4";

        try
        {
            await _state.SetStateAsync(tmdbId, mediaType, TrailerState.Discovering, ct).ConfigureAwait(false);
            var youtubeUrl = await ResolveTrailerUrlAsync(tmdbId, mediaType, lang, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(youtubeUrl))
            {
                // No obtainable trailer for this title — permanent, so the client stops asking.
                _metrics.Increment("trailer.resolve.none");
                await _state.SetStateAsync(tmdbId, mediaType, TrailerState.FailedPermanent, ct, "no trailer url").ConfigureAwait(false);
                return TrailerState.FailedPermanent;
            }

            // 1. yt-dlp: best stream capped at 1080p (480p read blurry on living-room screens).
            // YouTube only serves >720p as separate DASH video+audio, so prefer a merged pick and
            // fall down the ladder to progressive formats when merging isn't possible.
            await _state.SetStateAsync(tmdbId, mediaType, TrailerState.Downloading, ct).ConfigureAwait(false);
            var dl = await RunAsync("yt-dlp",
                $"-f \"bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/best[height<=1080][ext=mp4]/best[height<=1080]/best\" " +
                $"--merge-output-format mp4 --no-playlist --no-warnings --no-progress -o \"{tmp}.%(ext)s\" \"{youtubeUrl}\"",
                DownloadTimeoutMs, ct).ConfigureAwait(false);
            if (!dl || !File.Exists(tmpDl))
            {
                _metrics.Increment("trailer.download.error");
                await _state.SetStateAsync(tmdbId, mediaType, TrailerState.FailedTemporary, ct, "download failed").ConfigureAwait(false);
                return TrailerState.FailedTemporary;
            }

            // 2. ffmpeg: FULL length (no time cap — users watch the whole trailer) at up to 1080p
            // (never upscaled) and a bitrate that stays crisp on a TV; the LAN carries it easily.
            // Written to a ".part" sidecar and moved into place only on success: GetCachedPath
            // treats any existing outPath as servable, so a killed/timed-out transcode must never
            // leave a truncated file AT the cache path (it would be served as "Ready" forever).
            await _state.SetStateAsync(tmdbId, mediaType, TrailerState.Transcoding, ct).ConfigureAwait(false);
            TryDelete(partPath);
            var tx = await RunAsync("ffmpeg",
                $"-y -i \"{tmpDl}\" -vf scale=-2:'min(1080,ih)' -b:v 4500k -maxrate 6000k -bufsize 9000k " +
                $"-c:v libx264 -preset veryfast -c:a aac -b:a 160k -movflags +faststart \"{partPath}\"",
                TranscodeTimeoutMs, ct).ConfigureAwait(false);
            if (!tx || !File.Exists(partPath) || new FileInfo(partPath).Length == 0)
            {
                TryDelete(partPath);
                _metrics.Increment("trailer.transcode.error");
                await _state.SetStateAsync(tmdbId, mediaType, TrailerState.FailedTemporary, ct, "transcode failed").ConfigureAwait(false);
                return TrailerState.FailedTemporary;
            }

            File.Move(partPath, outPath, overwrite: true);

            // Drop the superseded generations (the 480p ".full" clip and the legacy 60s clip).
            TryDelete(Path.Combine(CacheDir, $"{tmdbId}.full.mp4"));
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
            TryDelete(tmpDl);
            TryDelete(partPath);
        }
    }

    /// <summary>The trailer sources tried, in admin-configured order, until one yields a URL.</summary>
    /// <remarks>
    /// Each source records whether it produced a URL (<c>trailer.resolve.{source}.ok</c> /
    /// <c>.miss</c>), so the dashboard can show where trailers actually come from — and, when one is
    /// missing, which sources were asked and came back empty.
    /// </remarks>
    private async Task<string?> ResolveTrailerUrlAsync(int tmdbId, MediaType mediaType, string? lang, CancellationToken ct)
    {
        var order = (Plugin.Instance?.Configuration?.TrailerSourceOrder ?? DefaultSourceOrder)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // One context for whichever sources need the catalog row, opened only if they do.
        CatalogItem? row = null;
        async Task<CatalogItem?> RowAsync()
        {
            if (row is not null)
            {
                return row;
            }

            await using var db = _factory.Create();
            row = await db.CatalogItems
                .AsNoTracking()
                .Where(c => c.TmdbId == tmdbId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            return row;
        }

        foreach (var source in order)
        {
            var url = source.ToLowerInvariant() switch
            {
                "tmdb" => await FromTmdbAsync(tmdbId, mediaType, lang, ct).ConfigureAwait(false),
                "jellyfin" => FromJellyfin(await RowAsync().ConfigureAwait(false)),
                "stored" => (await RowAsync().ConfigureAwait(false))?.TrailerUrl,
                "search" => FromSearch(await RowAsync().ConfigureAwait(false)),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(url))
            {
                _metrics.Increment($"trailer.resolve.{source.ToLowerInvariant()}.ok");
                return url;
            }

            _metrics.Increment($"trailer.resolve.{source.ToLowerInvariant()}.miss");
        }

        return null;
    }

    private async Task<string?> FromTmdbAsync(int tmdbId, MediaType mediaType, string? lang, CancellationToken ct)
    {
        if (!_tmdb.IsConfigured)
        {
            return null;
        }

        var picked = await _tmdb.GetTrailerUrlAsync(tmdbId, mediaType, lang, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            return picked;
        }

        // The full enrichment is TMDB-backed too, so this is usually moot — but it costs one call
        // and occasionally carries a URL the videos endpoint did not.
        var enrichment = await _tmdb.EnrichAsync(tmdbId, mediaType, ct).ConfigureAwait(false);
        return enrichment?.TrailerUrl;
    }

    /// <summary>
    /// The trailer Jellyfin's own metadata providers already found for this title.
    /// </summary>
    /// <remarks>
    /// Free and already on disk for anything in the library — no API key, no request. Only library
    /// items have one, so this silently yields nothing for discovery rows.
    /// </remarks>
    private string? FromJellyfin(CatalogItem? row)
    {
        if (row?.JellyfinItemId is not { } id || id == Guid.Empty)
        {
            return null;
        }

        try
        {
            var trailers = _libraryManager.GetItemById(id)?.RemoteTrailers;
            return trailers?.Select(t => t.Url).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orca Engine: could not read Jellyfin trailers for {Id}.", id);
            return null;
        }
    }

    /// <summary>
    /// A yt-dlp search expression, used verbatim as the download input.
    /// </summary>
    /// <remarks>
    /// Off by default: a text search can return a fan edit, a reaction video or a review, and a
    /// confidently wrong trailer is worse than none. Enabled, it is the only source that can cover
    /// a title TMDB has no video for at all.
    /// </remarks>
    private static string? FromSearch(CatalogItem? row)
    {
        if (string.IsNullOrWhiteSpace(row?.Title))
        {
            return null;
        }

        var year = row.ProductionYear is > 0 ? $" {row.ProductionYear}" : string.Empty;
        return $"ytsearch1:{row.Title}{year} official trailer";
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
    private Task<string?> RunCaptureAsync(string fileName, string args, int timeoutMs, CancellationToken ct) =>
        ExternalProcess.CaptureAsync(fileName, args, timeoutMs, _logger, ct);

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

    private static void TryKill(Process p) => ExternalProcess.TryKill(p);

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
