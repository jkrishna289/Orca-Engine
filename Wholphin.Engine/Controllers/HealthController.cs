using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// Health/version endpoint so clients can detect the engine and degrade gracefully when absent.
/// </summary>
[ApiController]
[Route("OrcaEngine")]
[Produces("application/json")]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Returns plugin liveness and version info. Anonymous so the client can probe for the engine.
    /// </summary>
    [HttpGet("Health")]
    [AllowAnonymous]
    public ActionResult<HealthResponse> GetHealth()
    {
        return new HealthResponse
        {
            Status = "ok",
            Plugin = Plugin.Instance?.Name ?? "Orca Engine",
            Version = Plugin.Instance?.Version?.ToString() ?? "unknown",
            SchemaVersion = 1
        };
    }

    /// <summary>
    /// Response body for <see cref="GetHealth"/>.
    /// </summary>
    public class HealthResponse
    {
        /// <summary>Gets or sets the liveness status.</summary>
        public string Status { get; set; } = "ok";

        /// <summary>Gets or sets the plugin name.</summary>
        public string Plugin { get; set; } = string.Empty;

        /// <summary>Gets or sets the plugin version.</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Gets or sets the engine data/schema version.</summary>
        public int SchemaVersion { get; set; }
    }
}
