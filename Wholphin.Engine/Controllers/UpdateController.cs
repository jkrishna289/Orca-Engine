using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Update;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// LAN update channel for the Orca X app. <c>Latest</c> mirrors the newest GitHub (pre)release in the
/// GitHub JSON shape the app's UpdateChecker already parses — with every asset URL rewritten to this
/// server's <c>Apk</c> endpoint, which serves the file from the engine's disk cache (downloaded from
/// GitHub once, then reused by every client). AllowAnonymous like the trailer/image endpoints: the
/// app's downloader carries no Jellyfin auth header.
/// </summary>
[ApiController]
[Route("OrcaEngine/Update")]
public class UpdateController : ControllerBase
{
    private readonly IAppUpdateService _updates;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateController"/> class.
    /// </summary>
    /// <param name="updates">The app-update proxy service.</param>
    public UpdateController(IAppUpdateService updates)
    {
        _updates = updates;
    }

    /// <summary>The latest app release in GitHub-release JSON shape, with LAN asset URLs.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The release metadata, or 404 when unavailable.</returns>
    [HttpGet("Latest")]
    [AllowAnonymous]
    public async Task<ActionResult<AppUpdateResponse>> GetLatest(CancellationToken ct)
    {
        var release = await _updates.GetLatestAsync(ct).ConfigureAwait(false);
        if (release is null)
        {
            return NotFound();
        }

        var baseUrl = $"{Request.Scheme}://{Request.Host}/OrcaEngine/Update/Apk";
        return new AppUpdateResponse
        {
            Name = release.Name,
            TagName = release.TagName,
            PublishedAt = release.PublishedAt,
            Body = release.Body,
            Assets = release.Assets.Select(a => new AppUpdateAssetResponse
            {
                Name = a.Name,
                Size = a.Size,
                BrowserDownloadUrl = $"{baseUrl}/{Uri.EscapeDataString(release.TagName)}/{Uri.EscapeDataString(a.Name)}",
            }).ToList(),
        };
    }

    /// <summary>Streams a release APK from the server's disk cache (fetched from GitHub on first request).</summary>
    /// <param name="tag">The release tag.</param>
    /// <param name="assetName">The APK asset name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The APK, or 404 when it can't be resolved.</returns>
    [HttpGet("Apk/{tag}/{assetName}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetApk(string tag, string assetName, CancellationToken ct)
    {
        var path = await _updates.GetApkPathAsync(tag, assetName, ct).ConfigureAwait(false);
        return path is null
            ? NotFound()
            : PhysicalFile(path, "application/vnd.android.package-archive", assetName, enableRangeProcessing: true);
    }
}

/// <summary>
/// GitHub-release-shaped payload (snake_case keys via <see cref="JsonPropertyNameAttribute"/> — the
/// host serializes PascalCase by default, but the app parses the GitHub field names verbatim).
/// </summary>
public class AppUpdateResponse
{
    /// <summary>Gets or sets the human release title.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the git tag (the app parses the version from this when the title isn't one).</summary>
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    /// <summary>Gets or sets the publish timestamp.</summary>
    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }

    /// <summary>Gets or sets the markdown release notes.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>Gets or sets the assets with LAN-rewritten download URLs.</summary>
    [JsonPropertyName("assets")]
    public List<AppUpdateAssetResponse> Assets { get; set; } = new();
}

/// <summary>A single asset entry in <see cref="AppUpdateResponse"/>.</summary>
public class AppUpdateAssetResponse
{
    /// <summary>Gets or sets the asset file name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the LAN download URL (this server's <c>Update/Apk</c> endpoint).</summary>
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the asset size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
}
