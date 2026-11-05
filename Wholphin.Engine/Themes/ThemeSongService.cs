using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Http;
using Wholphin.Engine.Integrations.Tmdb;
using Wholphin.Engine.Trailer;
using Wholphin.Engine.Trailer.Sources;

namespace Wholphin.Engine.Themes;

/// <summary>Theme songs for series, in the library or not, kept as MP3s the app can play on a show's page.</summary>
public interface IThemeSongService
{
    /// <summary>The cached MP3 for a series, or null when there isn't one yet.</summary>
    string? CachedPath(int tmdbId);

    /// <summary>
    /// Queues a series' theme to be found and downloaded. Does nothing when it's already cached, already
    /// queued, or was looked for recently and not found.
    /// </summary>
    void Enqueue(int tmdbId, string? title = null);
}

/// <summary>
/// Finds a series' theme and keeps it as <c>{DataPath}/wholphin-engine/themes/{tmdbId}.mp3</c>. Sources,
/// first that has it wins: ThemerrDB (a curated YouTube link, by TMDB id), then a YouTube search for the
/// show's theme or title song, scored so a review or a trailer never stands in for it. Plex's theme
/// server is deliberately not a source: it's Plex's own, not published for anyone else to use. One
/// worker fetches one show at a time, so a home screen full of new shows queues up rather than running a
/// dozen yt-dlp processes at once.
/// </summary>
public sealed class ThemeSongService : IThemeSongService, IDisposable
{
    private const string PrintFormat = "%(id)s\\t%(title)s\\t%(channel)s\\t%(duration)s";
    private const int SearchTimeoutMs = 30_000;
    private const int DownloadTimeoutMs = 180_000;

    /// <summary>A theme plays while the show's page is open; a minute and a half covers the opening.</summary>
    private const int MaxSeconds = 90;

    /// <summary>Nothing anywhere: themes get added, so look again in a few days.</summary>
    private static readonly TimeSpan NoneFoundFor = TimeSpan.FromDays(3);

    /// <summary>Something failed on the way (network, yt-dlp): try again soon.</summary>
    private static readonly TimeSpan FailedFor = TimeSpan.FromHours(1);

    private readonly Channel<(int TmdbId, string? Title)> _queue =
        Channel.CreateUnbounded<(int TmdbId, string? Title)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<int, byte> _queued = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly IApplicationPaths _paths;
    private readonly IHttpClientFactory _http;
    private readonly ITmdbClient _tmdb;
    private readonly ICache _cache;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<ThemeSongService> _logger;
    private int _started;

    /// <summary>Initializes a new instance of the <see cref="ThemeSongService"/> class.</summary>
    public ThemeSongService(
        IApplicationPaths paths,
        IHttpClientFactory http,
        ITmdbClient tmdb,
        ICache cache,
        IEngineMetrics metrics,
        ILogger<ThemeSongService> logger)
    {
        _paths = paths;
        _http = http;
        _tmdb = tmdb;
        _cache = cache;
        _metrics = metrics;
        _logger = logger;
    }

    private string Dir => Path.Combine(_paths.DataPath, "wholphin-engine", "themes");

    /// <inheritdoc />
    public string? CachedPath(int tmdbId)
    {
        var path = PathFor(tmdbId);
        return tmdbId > 0 && File.Exists(path) ? path : null;
    }

    /// <inheritdoc />
    public void Enqueue(int tmdbId, string? title = null)
    {
        if (tmdbId <= 0 || CachedPath(tmdbId) is not null || _cache.TryGet<bool>(MissKey(tmdbId), out _) || !_queued.TryAdd(tmdbId, 0))
        {
            return;
        }

        _queue.Writer.TryWrite((tmdbId, title));
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(() => WorkAsync(_stop.Token));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private static string MissKey(int tmdbId) => "theme-miss:" + tmdbId.ToString(CultureInfo.InvariantCulture);

    private string PathFor(int tmdbId) => Path.Combine(Dir, tmdbId.ToString(CultureInfo.InvariantCulture) + ".mp3");

    private async Task WorkAsync(CancellationToken ct)
    {
        await foreach (var (tmdbId, title) in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var source = await FetchAsync(tmdbId, title, ct).ConfigureAwait(false);
                _metrics.Increment("theme." + (source ?? "none"));
                if (source is null)
                {
                    _cache.Set(MissKey(tmdbId), true, NoneFoundFor);
                }
                else
                {
                    _logger.LogInformation("Orca Engine: theme for tmdb {TmdbId} from {Source}.", tmdbId, source);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _metrics.Increment("theme.error");
                _logger.LogWarning(ex, "Orca Engine: theme fetch failed for tmdb {TmdbId}.", tmdbId);
                _cache.Set(MissKey(tmdbId), true, FailedFor);
            }
            finally
            {
                _queued.TryRemove(tmdbId, out _);
            }
        }
    }

    /// <summary>Tries each source in turn; returns which one had it, or null when none did.</summary>
    private async Task<string?> FetchAsync(int tmdbId, string? title, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);

        if (await ThemerrDbAsync(tmdbId, ct).ConfigureAwait(false) is { } curated
            && await FromYouTubeAsync(curated, tmdbId, ct).ConfigureAwait(false))
        {
            return "themerrdb";
        }

        var name = (await _tmdb.GetSeriesDetailAsync(tmdbId, ct).ConfigureAwait(false))?.Name ?? title;
        if (!string.IsNullOrWhiteSpace(name)
            && await SearchYouTubeAsync(name, ct).ConfigureAwait(false) is { } found
            && await FromYouTubeAsync(found, tmdbId, ct).ConfigureAwait(false))
        {
            return "youtube";
        }

        return null;
    }

    private async Task<string?> ThemerrDbAsync(int tmdbId, CancellationToken ct)
    {
        using var client = _http.CreateClient(OrcaMetricsHandler.ClientName);
        using var response = await client.GetAsync(
            $"https://app.lizardbyte.dev/ThemerrDB/tv_shows/themoviedb/{tmdbId.ToString(CultureInfo.InvariantCulture)}.json", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("youtube_theme_url", out var url) && url.ValueKind == JsonValueKind.String
            && url.GetString() is { Length: > 0 } link && link.StartsWith("https://www.youtube.com/", StringComparison.Ordinal)
            ? link
            : null;
    }

    private async Task<string?> SearchYouTubeAsync(string name, CancellationToken ct)
    {
        var clean = name.Replace("\"", string.Empty, StringComparison.Ordinal).Trim();
        var candidates = new List<TrailerCandidate>();
        foreach (var query in new[] { $"{clean} theme song", $"{clean} title track" })
        {
            var stdout = await ExternalProcess.CaptureAsync(
                "yt-dlp",
                ["--skip-download", "--no-warnings", "--flat-playlist", "--print", PrintFormat, "--", "ytsearch8:" + query],
                SearchTimeoutMs,
                _logger,
                ct).ConfigureAwait(false);
            candidates.AddRange(YtDlpSearchTrailerSource.ParseLines(stdout));
        }

        return ThemeCandidates.Best(candidates, name) is { } best ? "https://www.youtube.com/watch?v=" + best.Id : null;
    }

    /// <summary>Downloads a video's audio and keeps its first <see cref="MaxSeconds"/> seconds, faded out, as MP3.</summary>
    private async Task<bool> FromYouTubeAsync(string url, int tmdbId, CancellationToken ct)
    {
        var src = Path.Combine(Dir, tmdbId.ToString(CultureInfo.InvariantCulture) + ".src");
        var part = PathFor(tmdbId) + ".part.mp3";
        try
        {
            // argv plus "--" keeps the URL a URL, never an option. Quiet tools: stderr is redirected
            // but not drained, and a chatty process would fill the pipe and stall until the timeout.
            var dl = await ExternalProcess.CaptureAsync(
                "yt-dlp",
                ["-f", "bestaudio/best", "--no-playlist", "--max-filesize", "80M", "--quiet", "--no-warnings", "--no-progress", "-o", src + ".%(ext)s", "--", url],
                DownloadTimeoutMs,
                _logger,
                ct).ConfigureAwait(false);
            var downloaded = Directory.GetFiles(Dir, Path.GetFileName(src) + ".*").FirstOrDefault();
            if (dl is null || downloaded is null)
            {
                return false;
            }

            var fadeAt = (MaxSeconds - 4).ToString(CultureInfo.InvariantCulture);
            var tx = await ExternalProcess.CaptureAsync(
                "ffmpeg",
                ["-hide_banner", "-loglevel", "error", "-nostats", "-y", "-i", downloaded, "-vn", "-t", MaxSeconds.ToString(CultureInfo.InvariantCulture),
                 "-af", $"afade=t=out:st={fadeAt}:d=4", "-c:a", "libmp3lame", "-b:a", "128k", part],
                DownloadTimeoutMs,
                _logger,
                ct).ConfigureAwait(false);
            return tx is not null && Publish(part, tmdbId);
        }
        finally
        {
            foreach (var leftover in Directory.GetFiles(Dir, Path.GetFileName(src) + ".*"))
            {
                File.Delete(leftover);
            }

            File.Delete(part);
        }
    }

    /// <summary>Moves a finished download into place; a stub (an error page, a cut-off file) is thrown away.</summary>
    private bool Publish(string part, int tmdbId)
    {
        if (!File.Exists(part) || new FileInfo(part).Length < 16_000)
        {
            File.Delete(part);
            return false;
        }

        File.Move(part, PathFor(tmdbId), overwrite: true);
        return true;
    }
}

/// <summary>
/// Picks a show's theme out of YouTube search results. A search for "Show theme song" is free text and
/// returns trailers, reviews, fan covers and episode clips alongside the song; only a result named after
/// the show and called a theme, title track, opening or soundtrack, of song length, is taken.
/// </summary>
internal static class ThemeCandidates
{
    private static readonly Regex Wanted = new(
        @"\b(theme|title (song|track|music)|opening|intro song|main title|signature tune|ost|soundtrack)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Unwanted = new(
        @"\b(trailer|teaser|review|reaction|explained|episode|ep \d+|scene|promo|interview|behind the scenes|making of|recap|bts|cover|karaoke|shorts?|status|8d|slowed|reverb|remix|piano|guitar|tutorial|ringtone|1 hour|10 hours?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>The best acceptable candidate, or null when none is plainly this show's theme.</summary>
    public static TrailerCandidate? Best(IEnumerable<TrailerCandidate> candidates, string show)
    {
        var name = Words(show);
        if (name.Length == 0)
        {
            return null;
        }

        TrailerCandidate? best = null;
        var bestScore = 0;
        foreach (var c in candidates)
        {
            var title = Words(c.Title);
            var spaced = " " + title + " ";
            if (!spaced.Contains(" " + name + " ", StringComparison.Ordinal) || !Wanted.IsMatch(title) || Unwanted.IsMatch(title)
                || c.DurationSeconds is < 20 or > 420)
            {
                continue;
            }

            var score = 10
                + (title.StartsWith(name, StringComparison.Ordinal) ? 5 : 0)
                + (c.DurationSeconds is >= 30 and <= 240 ? 5 : 0);
            if (score > bestScore)
            {
                (best, bestScore) = (c, score);
            }
        }

        return best;
    }

    private static string Words(string s) =>
        NonWord.Replace(s.ToLowerInvariant().Replace("'", string.Empty, StringComparison.Ordinal).Replace("’", string.Empty, StringComparison.Ordinal), " ").Trim();
}
