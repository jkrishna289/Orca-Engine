using System.Text.Json;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wholphin.Engine.Controllers;
using Wholphin.Engine.Playback;
using Xunit;

namespace Wholphin.Engine.Tests;

public sealed class PlaybackControllerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PlaybackProgressService _progress;
    private readonly FakeAuthorization _authorization = new();
    private readonly FakeBridge _bridge = new();
    private readonly PlaybackController _controller;

    public PlaybackControllerTests()
    {
        _connection.Open();
        var factory = new TestDbFactory(_connection);
        using var db = factory.Create();
        db.Database.EnsureCreated();
        _progress = new PlaybackProgressService(factory);
        _controller = new PlaybackController(_progress, _bridge, _authorization)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task AuthenticatedUserOwnsProgressAndBodyCannotSpoofAnotherUser()
    {
        var userId = Guid.NewGuid();
        var victimId = Guid.NewGuid();
        _authorization.UserId = userId;
        var request = JsonSerializer.Deserialize<StartPlaybackRequest>("{\"TmdbId\":550,\"MediaType\":\"movie\",\"UserId\":\"" + victimId + "\"}")!;
        var start = await _controller.Start(request, CancellationToken.None);
        var response = Assert.IsType<PlaybackProgressResponse>(Assert.IsType<OkObjectResult>(start.Result).Value);
        Assert.Equal(userId, (await _progress.GetAsync(userId, new PlaybackIdentity(550, "movie")))!.UserId);
        Assert.Null(await _progress.GetAsync(victimId, new PlaybackIdentity(550, "movie")));
        _authorization.UserId = victimId;
        Assert.IsType<NotFoundResult>(await _controller.Report(response.SessionId, new PlaybackProgressUpdate(1, 40000, 100000, false, true), CancellationToken.None));
    }

    [Fact]
    public async Task ApiTokenWithoutUserCannotReadOrWritePlaybackState()
    {
        _authorization.UserId = Guid.Empty;
        Assert.IsType<UnauthorizedResult>((await _controller.Start(new StartPlaybackRequest { TmdbId = 550 }, CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await _controller.Get(550, "movie")).Result);
        Assert.IsType<UnauthorizedResult>(await _controller.Report(Guid.NewGuid(), new PlaybackProgressUpdate(1, 10, 100, false, false), CancellationToken.None));
    }

    [Fact]
    public async Task SavedReportRemainsSuccessfulWhenLibraryHandoffTemporarilyFails()
    {
        _authorization.UserId = Guid.NewGuid();
        var row = await _progress.StartAsync(_authorization.UserId, new PlaybackIdentity(550, "movie"));
        _bridge.FailReconcile = true;
        Assert.IsType<OkObjectResult>(await _controller.Report(row.ActiveSessionId, new PlaybackProgressUpdate(1, 40000, 100000, false, true), CancellationToken.None));
        Assert.Equal(40000, (await _progress.GetAsync(_authorization.UserId, new PlaybackIdentity(550, "movie")))!.PositionMs);
        Assert.Equal(0, _bridge.ReconcileCalls);
    }

    [Fact]
    public void PascalCaseWireContractIsStableEvenWithWebSerializerDefaults()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<StartPlaybackRequest>("""{"TmdbId":550,"MediaType":"tv","Season":1,"Episode":2,"Title":"Example","Year":2020}""", options)!;
        Assert.Equal(550, request.TmdbId);
        Assert.Equal(2, request.Episode);
        var update = JsonSerializer.Deserialize<PlaybackProgressUpdate>("""{"Sequence":8,"PositionMs":40000,"DurationMs":100000,"Completed":false,"Stopped":true}""", options)!;
        Assert.Equal(40000, update.PositionMs);
        Assert.True(update.Stopped);
        var id = Guid.NewGuid();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new PlaybackProgressResponse(id, 40000, 100000, false, null), options));
        Assert.Equal(id.ToString(), document.RootElement.GetProperty("SessionId").GetString());
        Assert.Equal(40000, document.RootElement.GetProperty("PositionMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("JellyfinItemId").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("positionMs", out _));
    }

    [Fact]
    public async Task ContinueWatchingCannotBeReadBySpoofingTheHomeUserId()
    {
        var home = new HomeController(null!, null!, null!, _authorization)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var owner = Guid.NewGuid();
        _authorization.UserId = Guid.Empty;
        Assert.IsType<UnauthorizedResult>((await home.GetHome(null, owner)).Result);
        Assert.IsType<UnauthorizedResult>((await home.GetBootstrap(null, owner)).Result);
        _authorization.UserId = Guid.NewGuid();
        Assert.IsType<ForbidResult>((await home.GetHome(null, owner)).Result);
        Assert.IsType<ForbidResult>((await home.GetBootstrap(null, owner)).Result);
    }

    [Fact]
    public async Task MissingProgressReturnsJsonNullWith200InsteadOfNoContent()
    {
        _authorization.UserId = Guid.NewGuid();
        var result = Assert.IsType<JsonResult>((await _controller.Get(550, "movie")).Result);
        Assert.Null(result.Value);
        Assert.Equal(200, result.StatusCode);
    }

    private sealed class FakeAuthorization : IAuthorizationContext
    {
        public Guid UserId { get; set; }
        private AuthorizationInfo Info => new()
        {
            User = UserId == Guid.Empty ? null : new Jellyfin.Database.Implementations.Entities.User("viewer", "auth", "reset") { Id = UserId },
        };
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext context) => Task.FromResult(Info);
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest request) => Task.FromResult(Info);
    }

    private sealed class FakeBridge : IJellyfinProgressBridge
    {
        public bool FailReconcile { get; set; }
        public int ReconcileCalls { get; private set; }
        public Task ReconcileAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReconcileUserAsync(Guid userId, CancellationToken ct = default)
        {
            ReconcileCalls++;
            return FailReconcile ? Task.FromException(new InvalidOperationException("Jellyfin unavailable")) : Task.CompletedTask;
        }
        public Task ObserveExistingAsync(Guid userId, PlaybackIdentity identity, CancellationToken ct = default) => Task.CompletedTask;
    }
}
