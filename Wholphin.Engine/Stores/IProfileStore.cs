using System;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Entities;

namespace Wholphin.Engine.Stores;

/// <summary>
/// Persists per-user personalization profiles.
/// </summary>
public interface IProfileStore
{
    /// <summary>Gets a user's profile, or null if none exists yet.</summary>
    Task<UserProfile?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Inserts or updates a user's profile.</summary>
    Task SaveAsync(UserProfile profile, CancellationToken ct = default);
}
