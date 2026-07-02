using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Stores;

/// <summary>
/// EF Core implementation of <see cref="IProfileStore"/>.
/// </summary>
public class ProfileStore : IProfileStore
{
    private readonly IWholphinDbContextFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileStore"/> class.
    /// </summary>
    public ProfileStore(IWholphinDbContextFactory factory) => _factory = factory;

    /// <inheritdoc />
    public async Task<UserProfile?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        return await db.UserProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(UserProfile profile, CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        var existing = await db.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == profile.UserId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.UserProfiles.Add(profile);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(profile);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
