using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Update;

/// <summary>
/// Proxies Orca X app releases from GitHub so TV clients update over the LAN: release metadata is
/// cached in memory and APK assets are downloaded once to a durable disk cache, then served to every
/// device from the server — no repeated GitHub downloads per client.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>Gets the latest (pre)release of the Orca X app from GitHub (cached).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest release, or null when GitHub is unreachable or there are no releases.</returns>
    Task<AppRelease?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves a release APK to a local file path, downloading it into the disk cache on first
    /// request (single-flight) and pruning caches of older releases.
    /// </summary>
    /// <param name="tag">The release tag (e.g. <c>v0.1.0</c>).</param>
    /// <param name="assetName">The APK asset file name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cached file path, or null when the asset can't be resolved or downloaded.</returns>
    Task<string?> GetApkPathAsync(string tag, string assetName, CancellationToken ct = default);
}

/// <summary>A published app release (subset of the GitHub release fields the client consumes).</summary>
public class AppRelease
{
    /// <summary>Gets or sets the git tag (e.g. <c>v0.1.0</c>) — the parseable version source.</summary>
    public string TagName { get; set; } = string.Empty;

    /// <summary>Gets or sets the human release title (may not be a parseable version).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the ISO-8601 publish timestamp.</summary>
    public string? PublishedAt { get; set; }

    /// <summary>Gets or sets the markdown release notes.</summary>
    public string? Body { get; set; }

    /// <summary>Gets or sets the downloadable assets (APKs).</summary>
    public List<AppReleaseAsset> Assets { get; set; } = new();
}

/// <summary>A single downloadable release asset.</summary>
public class AppReleaseAsset
{
    /// <summary>Gets or sets the asset file name (e.g. <c>OrcaX-debug-arm64-v8a.apk</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the asset size in bytes.</summary>
    public long Size { get; set; }
}
