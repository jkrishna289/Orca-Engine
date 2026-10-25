using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wholphin.Engine.Controllers;
using Xunit;

namespace Wholphin.Engine.Tests;

public sealed class UserAuthorizationTests
{
    [Fact]
    public async Task SuppliedUserIdRequiresThatAuthenticatedUser()
    {
        var self = Guid.NewGuid();
        var controller = new Probe { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        // Generic (no-user) calls stay anonymous.
        Assert.Null(await controller.AuthorizeUserAsync(new Auth(Guid.Empty), null));
        Assert.Null(await controller.AuthorizeUserAsync(new Auth(Guid.Empty), Guid.Empty));
        // A user id alone does not grant access; neither does someone else's session.
        Assert.IsType<UnauthorizedResult>(await controller.AuthorizeUserAsync(new Auth(Guid.Empty), self));
        Assert.IsType<ForbidResult>(await controller.AuthorizeUserAsync(new Auth(Guid.NewGuid()), self));
        Assert.Null(await controller.AuthorizeUserAsync(new Auth(self), self));
    }

    private sealed class Probe : ControllerBase;

    private sealed class Auth(Guid userId) : IAuthorizationContext
    {
        private AuthorizationInfo Info => new()
        {
            User = userId == Guid.Empty ? null : new Jellyfin.Database.Implementations.Entities.User("viewer", "auth", "reset") { Id = userId },
        };
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext context) => Task.FromResult(Info);
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest request) => Task.FromResult(Info);
    }
}
