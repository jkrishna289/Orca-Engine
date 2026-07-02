using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Home;
using Wholphin.Engine.Settings;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Serves the home <see cref="RenderBundle"/> built from the real catalog.
/// </summary>
[ApiController]
[Route("OrcaEngine")]
[Produces("application/json")]
public class HomeController : ControllerBase
{
    private readonly HomeService _home;
    private readonly ISettingsService _settings;
    private readonly INewSinceProvider _newSince;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeController"/> class.
    /// </summary>
    public HomeController(HomeService home, ISettingsService settings, INewSinceProvider newSince)
    {
        _home = home;
        _settings = settings;
        _newSince = newSince;
    }

    /// <summary>Returns the home render bundle (personalized when a user is supplied).</summary>
    /// <param name="supported">Comma-separated CardType names the client supports (capability gating).</param>
    /// <param name="userId">Optional Jellyfin user id; enables the personalized "For You" row.</param>
    /// <param name="rowSize">Items per row (1-100, default 20).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The home render bundle.</returns>
    [HttpGet("Home")]
    [AllowAnonymous]
    public async Task<ActionResult<RenderBundle>> GetHome(
        [FromQuery] string? supported,
        [FromQuery] Guid? userId,
        [FromQuery] int rowSize = 20,
        [FromQuery] bool inlineVideo = false,
        CancellationToken cancellationToken = default)
    {
        var capabilities = ParseCapabilities(supported, inlineVideo);
        return await _home.BuildAsync(capabilities, rowSize, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One-call bootstrap: contract version, server time, resolved remote config (settings +
    /// feature flags), and the first home rows. The single round-trip the app makes on launch.
    /// </summary>
    /// <param name="supported">Comma-separated CardType names the client supports (capability gating).</param>
    /// <param name="userId">Optional Jellyfin user id; enables personalization.</param>
    /// <param name="rowSize">Items per row (1-100, default 20).</param>
    /// <param name="inlineVideo">Whether the client can render inline billboard trailers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bootstrap payload.</returns>
    [HttpGet("Bootstrap")]
    [AllowAnonymous]
    public async Task<ActionResult<BootstrapResponse>> GetBootstrap(
        [FromQuery] string? supported,
        [FromQuery] Guid? userId,
        [FromQuery] int rowSize = 20,
        [FromQuery] bool inlineVideo = false,
        CancellationToken cancellationToken = default)
    {
        var capabilities = ParseCapabilities(supported, inlineVideo);
        var settings = await _settings.ResolveAsync(userId, cancellationToken).ConfigureAwait(false);
        var home = await _home.BuildAsync(capabilities, rowSize, userId, cancellationToken).ConfigureAwait(false);

        // Stamp "last seen" after building (the row was computed against the PREVIOUS visit) so the
        // next launch's "New Since You Were Away" reflects what arrived while the user was gone.
        if (userId is { } seen && seen != Guid.Empty)
        {
            await _newSince.MarkSeenAsync(seen, cancellationToken).ConfigureAwait(false);
        }

        return new BootstrapResponse
        {
            ContractVersion = home.ContractVersion,
            ServerTimeUtc = DateTime.UtcNow,
            Personalized = userId is { } u && u != Guid.Empty,
            Settings = settings,
            Home = home
        };
    }

    private static ClientCapabilities ParseCapabilities(string? supported, bool inlineVideo)
    {
        var capabilities = new ClientCapabilities { SupportsInlineVideo = inlineVideo };
        if (string.IsNullOrWhiteSpace(supported))
        {
            return capabilities;
        }

        foreach (var token in supported.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<CardType>(token, ignoreCase: true, out var cardType))
            {
                capabilities.SupportedCardTypes.Add(cardType);
            }
        }

        return capabilities;
    }

    /// <summary>One-call launch payload: contract metadata plus the first home rows.</summary>
    public class BootstrapResponse
    {
        /// <summary>Gets or sets the card-contract version the engine emitted.</summary>
        public int ContractVersion { get; set; }

        /// <summary>Gets or sets the server time (UTC) at bootstrap.</summary>
        public DateTime ServerTimeUtc { get; set; }

        /// <summary>Gets or sets a value indicating whether the home was personalized for a user.</summary>
        public bool Personalized { get; set; }

        /// <summary>Gets or sets the resolved engine settings + feature flags (remote config).</summary>
        public EngineSettings Settings { get; set; } = new();

        /// <summary>Gets or sets the home render bundle.</summary>
        public RenderBundle Home { get; set; } = new();
    }
}
