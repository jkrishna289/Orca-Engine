using System;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Requests;

/// <summary>The outcome of an engine-proxied request.</summary>
public enum RequestOutcome
{
    /// <summary>Jellyseerr accepted the request.</summary>
    Success,

    /// <summary>The requests feature is disabled in settings.</summary>
    Disabled,

    /// <summary>Jellyseerr is not configured (URL/API key missing).</summary>
    NotConfigured,

    /// <summary>The request payload was invalid (bad id or unsupported media type).</summary>
    InvalidInput,

    /// <summary>Jellyseerr rejected or could not service the request.</summary>
    Failed,
}

/// <summary>The result of an engine-proxied request.</summary>
public class RequestResult
{
    /// <summary>Gets or sets the outcome.</summary>
    public RequestOutcome Outcome { get; set; }

    /// <summary>Gets a value indicating whether the request succeeded.</summary>
    public bool Success => Outcome == RequestOutcome.Success;

    /// <summary>Gets or sets the resulting availability state for the title.</summary>
    public AvailabilityState Availability { get; set; }

    /// <summary>Gets or sets a human-readable message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Orchestrates engine-proxied media requests: validates the feature flag + integration, proxies
/// the request to Jellyseerr, records a request-affinity behavior signal, and reflects the new
/// availability in the catalog. The app never talks to Jellyseerr directly.
/// </summary>
public interface IRequestService
{
    /// <summary>Submits a request on behalf of a user.</summary>
    /// <param name="userId">The Jellyfin user id making the request.</param>
    /// <param name="tmdbId">The TMDB id of the title.</param>
    /// <param name="mediaType">Movie or Series.</param>
    /// <param name="title">Optional display title (stored on the new catalog row).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The request outcome.</returns>
    Task<RequestResult> RequestAsync(Guid userId, int tmdbId, MediaType mediaType, string? title, CancellationToken ct = default);
}
