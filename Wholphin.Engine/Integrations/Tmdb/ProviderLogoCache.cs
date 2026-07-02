using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Integrations.Tmdb;

/// <summary>
/// Default <see cref="IProviderLogoCache"/>. Caches provider logos under the engine data dir
/// (<c>{DataPath}/wholphin-engine/provider-logos/{id}{ext}</c>), keyed by TMDB provider id so a logo is
/// downloaded once and reused across every title on that service. Fail-soft everywhere.
/// </summary>
public class ProviderLogoCache : IProviderLogoCache
{
    private const string LogoBase = "https://image.tmdb.org/t/p/original";

    private readonly IApplicationPaths _appPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEngineMetrics _metrics;
    private readonly ILogger<ProviderLogoCache> _logger;

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderLogoCache"/> class.
    /// </summary>
    public ProviderLogoCache(
        IApplicationPaths appPaths,
        IHttpClientFactory httpClientFactory,
        IEngineMetrics metrics,
        ILogger<ProviderLogoCache> logger)
    {
        _appPaths = appPaths;
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
        _logger = logger;
    }

    private string CacheDir => Path.Combine(_appPaths.DataPath, "wholphin-engine", "provider-logos");

    /// <inheritdoc />
    public async Task EnsureCachedAsync(int providerId, string? logoPath, CancellationToken ct = default)
    {
        if (providerId <= 0 || string.IsNullOrWhiteSpace(logoPath))
        {
            return;
        }

        // Already cached (any extension) — nothing to do.
        if (Resolve(providerId).Path is not null)
        {
            return;
        }

        var ext = Path.GetExtension(logoPath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".png";
        }

        var outPath = Path.Combine(CacheDir, providerId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ext);

        var gate = _locks.GetOrAdd(providerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock (another caller may have just cached it).
            if (Resolve(providerId).Path is not null)
            {
                return;
            }

            Directory.CreateDirectory(CacheDir);

            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(LogoBase + logoPath, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _metrics.Increment("tmdb.providerlogo.error");
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                _metrics.Increment("tmdb.providerlogo.error");
                return;
            }

            // Write to a temp file then move into place so a partial download is never served.
            var tmp = outPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, outPath, overwrite: true);
            _metrics.Increment("tmdb.providerlogo.ok");
        }
        catch (Exception ex)
        {
            _metrics.Increment("tmdb.providerlogo.error");
            _logger.LogWarning(ex, "Orca Engine: provider-logo cache failed for provider {ProviderId}.", providerId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public (string? Path, string ContentType) Resolve(int providerId)
    {
        if (providerId <= 0 || !Directory.Exists(CacheDir))
        {
            return (null, "image/png");
        }

        try
        {
            var prefix = providerId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
            var match = Directory.EnumerateFiles(CacheDir, prefix + "*")
                .FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                                     && new FileInfo(f).Length > 0);
            return match is null ? (null, "image/png") : (match, ContentTypeFor(Path.GetExtension(match)));
        }
        catch
        {
            return (null, "image/png");
        }
    }

    private static string ContentTypeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
