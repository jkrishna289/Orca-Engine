using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Discovery;

/// <summary>The scope a discovery source runs in.</summary>
public enum DiscoverySourceScope
{
    /// <summary>Runs per user, driven by that user's taste profile.</summary>
    PerUser = 0,

    /// <summary>Runs once globally (results shared by all users, picks stored under Guid.Empty).</summary>
    Global = 1
}

/// <summary>
/// A pluggable candidate generator — the only stage of the discovery pipeline that talks to
/// external catalogs. Sources generate candidates and nothing else (no filtering, no scoring,
/// no writes); they are registered as a DI collection, so adding a future source (Trakt, MDBList,
/// an AI list, …) is a new class + registration with no pipeline changes.
/// </summary>
public interface IDiscoverySource
{
    /// <summary>Gets the stable source name (used in attributions, picks and metrics).</summary>
    string Name { get; }

    /// <summary>Gets the justification kind this source proposes.</summary>
    DiscoveryPickKind Kind { get; }

    /// <summary>Gets whether the source runs per user or globally.</summary>
    DiscoverySourceScope Scope { get; }

    /// <summary>Generates candidates for one run. Fail-soft: returns empty on any upstream failure.</summary>
    /// <param name="context">The run context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The proposed candidates (possibly empty).</returns>
    Task<IReadOnlyList<DiscoveryCandidate>> GatherAsync(DiscoveryContext context, CancellationToken ct = default);
}
