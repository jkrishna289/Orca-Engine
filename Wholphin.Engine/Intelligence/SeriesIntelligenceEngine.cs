using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Integrations.Arr;
using Wholphin.Engine.Personalization;

namespace Wholphin.Engine.Intelligence;

/// <summary>
/// Default <see cref="ISeriesIntelligenceEngine"/>. A thin composition over the per-series watch-state
/// service (Jellyfin), the personalization service (affinity → intent), and the *arr client (release
/// events). Holds no state of its own; every source it delegates to is already fail-soft.
/// </summary>
public sealed class SeriesIntelligenceEngine : ISeriesIntelligenceEngine
{
    private readonly ISeriesUserStateService _state;
    private readonly IPersonalizationService _personalization;
    private readonly IArrClient _arr;

    /// <summary>Initializes a new instance of the <see cref="SeriesIntelligenceEngine"/> class.</summary>
    /// <param name="state">The per-series user-state service.</param>
    /// <param name="personalization">The affinity/personalization service.</param>
    /// <param name="arr">The *arr client (release events).</param>
    public SeriesIntelligenceEngine(
        ISeriesUserStateService state,
        IPersonalizationService personalization,
        IArrClient arr)
    {
        _state = state;
        _personalization = personalization;
        _arr = arr;
    }

    /// <inheritdoc />
    public SeriesStateResult? GetSeriesState(Guid userId, Guid seriesJellyfinId)
        => _state.GetState(userId, seriesJellyfinId);

    /// <inheritdoc />
    public IReadOnlyList<StartedSeries> GetStartedSeries(Guid userId, int max, CancellationToken ct = default)
        => _state.GetStartedSeries(userId, max, ct);

    /// <inheritdoc />
    public IReadOnlySet<Guid> GetResumingSeriesIds(Guid userId, CancellationToken ct = default)
        => _state.GetResumingSeriesIds(userId, ct);

    /// <inheritdoc />
    public async Task<IntentScorer> GetIntentScorerAsync(Guid? userId, CancellationToken ct = default)
    {
        var affinity = userId is { } u && u != Guid.Empty
            ? await _personalization.GetAsync(u, ct).ConfigureAwait(false)
            : new AffinityVector();
        return new IntentScorer(affinity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArrHistoryEvent>> GetReleaseEventsAsync(DateTime sinceUtc, CancellationToken ct = default)
        => _arr.IsConfigured
            ? await _arr.GetHistoryAsync(sinceUtc, ct).ConfigureAwait(false)
            : Array.Empty<ArrHistoryEvent>();
}
