using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Mvc;

namespace Wholphin.Engine.Controllers;

/// <summary>Gates per-user state on endpoints that otherwise stay anonymous.</summary>
internal static class UserAuthorization
{
    /// <summary>
    /// Generic (no-user) calls stay anonymous. A user id supplied in the query or body alone must
    /// not grant access to that user's private state: the caller must be authenticated as that user.
    /// </summary>
    /// <returns>Null when allowed; otherwise the 401/403 result to return.</returns>
    public static async Task<ActionResult?> AuthorizeUserAsync(this ControllerBase controller, IAuthorizationContext authorization, Guid? userId)
    {
        if (userId is null || userId == Guid.Empty) return null;
        var authenticated = (await authorization.GetAuthorizationInfo(controller.HttpContext).ConfigureAwait(false)).UserId;
        if (authenticated == Guid.Empty) return controller.Unauthorized();
        return authenticated == userId ? null : controller.Forbid();
    }
}
