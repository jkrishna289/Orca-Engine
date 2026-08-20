using Wholphin.Engine.Behavior;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Enumerating Jellyfin accounts across host versions that disagree about how to ask.
/// </summary>
/// <remarks>
/// This is a regression suite with a specific incident behind it: 1.2.0.0 was built against Jellyfin
/// 10.11.0, whose <c>IUserManager</c> exposes a <c>Users</c> property, and deployed to 10.11.11,
/// which replaced it with <c>GetUsers()</c>. The watch-history import threw
/// <c>MissingMethodException</c> on its first line and the whole feature was dead on arrival.
/// </remarks>
public class JellyfinUsersTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    [Fact]
    public void ReadsThePropertyShape()
    {
        // Jellyfin 10.11.0
        Assert.Equal(new[] { A, B }, JellyfinUsers.AllIds(new PropertyHost()));
    }

    [Fact]
    public void ReadsTheGetUsersShape()
    {
        // Jellyfin 10.11.11
        Assert.Equal(new[] { A, B }, JellyfinUsers.AllIds(new GetUsersHost()));
    }

    [Fact]
    public void PrefersIdsWhenTheHostOffersThemDirectly()
    {
        var host = new IdsHost();

        Assert.Equal(new[] { A, B }, JellyfinUsers.AllIds(host));

        // The cheap path should not have materialized user objects to get there.
        Assert.False(host.UsersWasRead);
    }

    [Fact]
    public void AnUnknownHostShapeIsEmptyRatherThanAThrow()
    {
        // A future version that renames this again must degrade to "no users", not take the plugin
        // down — the failure that prompted this class was an exception, not an empty list.
        Assert.Empty(JellyfinUsers.AllIds(new object()));
        Assert.Empty(JellyfinUsers.AllIds(null!));
    }

    [Fact]
    public void SkipsNullsAndEmptyIdsAndDeduplicates()
    {
        Assert.Equal(new[] { A }, JellyfinUsers.AllIds(new MessyHost()));
    }

    private sealed class FakeUser
    {
        public FakeUser(Guid id) => Id = id;

        public Guid Id { get; }
    }

    private sealed class PropertyHost
    {
        public IEnumerable<FakeUser> Users => new[] { new FakeUser(A), new FakeUser(B) };
    }

    private sealed class GetUsersHost
    {
        public IEnumerable<FakeUser> GetUsers() => new[] { new FakeUser(A), new FakeUser(B) };
    }

    private sealed class IdsHost
    {
        public bool UsersWasRead { get; private set; }

        public IEnumerable<Guid> GetUsersIds() => new[] { A, B };

        public IEnumerable<FakeUser> GetUsers()
        {
            UsersWasRead = true;
            return Array.Empty<FakeUser>();
        }
    }

    private sealed class MessyHost
    {
        public IEnumerable<FakeUser?> GetUsers() =>
            new[] { new FakeUser(A), null, new FakeUser(Guid.Empty), new FakeUser(A) };
    }
}
