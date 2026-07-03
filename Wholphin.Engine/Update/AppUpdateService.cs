using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Caching;

namespace Wholphin.Engine.Update;

/// <summary>
/// Default <see cref="IAppUpdateService"/>: reads the Orca X repo's releases from the GitHub API
/// (prereleases included — alphas ship with the prerelease flag) and lazily mirrors APK assets into
/// <c>{DataPath}/wholphin-engine/apk-cache/{tag}/</c>. Every operation is fail-soft: a GitHub outage
/// just means "no update available" until the next check.
/// </summary>
public class AppUpdateService : IAppUpdateService
{
    /// <summary>The GitHub repository the Orca X app releases from.</summary>
    private const string Repo = "jkrishna289/OrcaX";

    /// <summary>How long the release metadata is cached before re-querying GitHub.</summary>
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromMinutes(15);

    private const string CacheKey = "appupdate:latest";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICache _cache;
    private readonly ILogger<AppUpdateService> _logger;
    private readonly string _cacheDir;

    // Single-flight: only one APK download at a time (assets are tens of MB; concurrent clients
    // asking for the same file must not each pull it from GitHub).
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="AppUpdateService"/> class.
    /// </summary>
    /// <param name="applicationPaths">The Jellyfin application paths.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="cache">The in-memory cache.</param>
    /// <param name="logger">The logger.</param>
    public AppUpdateService(
        IApplicationPaths applicationPaths,
        IHttpClientFactory httpClientFactory,
        ICache cache,
        ILogger<AppUpdateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _cacheDir = Path.Combine(applicationPaths.DataPath, "wholphin-engine", "apk-cache");
    }

    /// <inheritdoc />
    public async Task<AppRelease?> GetLatestAsync(CancellationToken ct = default)
    {
        if (_cache.TryGet<AppRelease>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=10");

            // GitHub's API rejects requests without a User-Agent.
            request.Headers.UserAgent.ParseAdd("OrcaEngine");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Orca Engine: app-release check failed ({Status}).", response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            // Newest first; drafts are invisible to anonymous calls, prereleases are wanted (alphas).
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var tag = el.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                var release = new AppRelease
                {
                    TagName = tag,
                    Name = el.TryGetProperty("name", out var n) ? n.GetString() : null,
                    PublishedAt = el.TryGetProperty("published_at", out var p) ? p.GetString() : null,
                    Body = el.TryGetProperty("body", out var b) ? b.GetString() : null,
                };

                if (el.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var an) ? an.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            release.Assets.Add(new AppReleaseAsset
                            {
                                Name = name,
                                Size = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                            });
                        }
                    }
                }

                _cache.Set(CacheKey, release, MetadataTtl);
                return release;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: app-release check failed.");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetApkPathAsync(string tag, string assetName, CancellationToken ct = default)
    {
        if (!IsSafeName(tag) || !IsSafeName(assetName)
            || !assetName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = Path.Combine(_cacheDir, tag, assetName);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        await _downloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: another request may have just finished the download.
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            Directory.CreateDirectory(Path.Combine(_cacheDir, tag));
            var tmp = path + ".tmp";
            var url = $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";
            _logger.LogInformation("Orca Engine: caching app APK {Asset} from {Tag}.", assetName, tag);

            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Orca Engine: APK download failed ({Status}) for {Url}.", response.StatusCode, url);
                return null;
            }

            await using (var target = File.Create(tmp))
            {
                await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
            PruneOtherTags(tag);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orca Engine: APK caching failed for {Tag}/{Asset}.", tag, assetName);
            return null;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    /// <summary>Deletes cached APK folders of releases other than the given tag (superseded).</summary>
    private void PruneOtherTags(string keepTag)
    {
        try
        {
            if (!Directory.Exists(_cacheDir))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(_cacheDir)
                         .Where(d => !string.Equals(Path.GetFileName(d), keepTag, StringComparison.OrdinalIgnoreCase)))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orca Engine: APK cache prune failed (non-fatal).");
        }
    }

    /// <summary>Rejects names that could escape the cache directory (path separators, dot-dot, etc.).</summary>
    private static bool IsSafeName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains("..", StringComparison.Ordinal)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
