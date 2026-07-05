using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Contracts;
using Wholphin.Engine.Data;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Intelligence;

namespace Wholphin.Engine.Home;

/// <summary>
/// Builds the "Continue the Story" row — the correct home for backlog that used to be mislabeled
/// "New Since You Were Away". It surfaces series the user has started but not finished, in two
/// user-state flavors from <see cref="ISeriesUserStateService"/>:
/// <list type="bullet">
///   <item><description><b>Skipped backlog</b> — a later season watched while an earlier one sits
///   entirely unwatched ("You skipped Season 1"); these lead the row.</description></item>
///   <item><description><b>Abandoned</b> — started, then left untouched for a long time.</description></item>
/// </list>
/// Actively in-progress series belong to "Continue Watching"; completed and never-started series are
/// excluded. Nothing here is presented as a temporal event.
/// </summary>
public sealed class ContinueTheStoryRowProvider : IRowProvider
{
    /// <summary>Cap on how many started series to classify (ordered by recent activity).</summary>
    private const int MaxCandidates = 40;

    private readonly IWholphinDbContextFactory _factory;
    private readonly ISeriesUserStateService _state;
    private readonly ILogger<ContinueTheStoryRowProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="ContinueTheStoryRowProvider"/> class.</summary>
    /// <param name="factory">Database context factory (resolves catalog rows for started series).</param>
    /// <param name="state">The per-series user-state service.</param>
    /// <param name="logger">The logger.</param>
    public ContinueTheStoryRowProvider(
        IWholphinDbContextFactory factory,
        ISeriesUserStateService state,
        ILogger<ContinueTheStoryRowProvider> logger)
    {
        _factory = factory;
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderRow>> BuildAsync(RowContext context, CancellationToken ct = default)
    {
        if (context.UserId is not { } userId || userId == Guid.Empty || !context.Settings.Features.ContinueTheStory)
        {
            return Array.Empty<ProviderRow>();
        }

        var started = _state.GetStartedSeries(userId, MaxCandidates, ct);
        if (started.Count == 0)
        {
            return Array.Empty<ProviderRow>();
        }

        // Classify each started series; keep only the continuity states (skipped gaps + abandoned).
        var candidates = new List<Continuity>();
        foreach (var s in started)
        {
            ct.ThrowIfCancellationRequested();
            var result = _state.GetState(userId, s.SeriesId);
            if (result is null)
            {
                continue;
            }

            if (result.State is SeriesUserState.SkippedBacklog or SeriesUserState.Abandoned)
            {
                candidates.Add(new Continuity(s.SeriesId, result, s.LastActivityUtc));
            }
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<ProviderRow>();
        }

        var byJellyfinId = candidates.ToDictionary(c => c.SeriesId);
        var seriesIds = candidates.Select(c => c.SeriesId).ToList();

        await using var db = _factory.Create();
        var catalog = await db.CatalogItems.AsNoTracking()
            .Where(c => c.JellyfinItemId != null && seriesIds.Contains(c.JellyfinItemId.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Gaps first (a skipped season is the more compelling, more surprising card), then abandoned;
        // within each, most-recently-active first.
        var ranked = catalog
            .Select(item => (Item: item, Continuity: byJellyfinId[item.JellyfinItemId!.Value]))
            .OrderBy(x => x.Continuity.Result.State == SeriesUserState.SkippedBacklog ? 0 : 1)
            .ThenByDescending(x => x.Continuity.LastActivityUtc)
            .Take(context.RowSize)
            .ToList();

        if (ranked.Count == 0)
        {
            return Array.Empty<ProviderRow>();
        }

        var reasons = ranked.ToDictionary(
            x => x.Item.Id,
            x => ReasonFor(x.Continuity.Result.State, x.Continuity.Result.GapSeasonNumber));

        return new[]
        {
            new ProviderRow
            {
                Id = "continuestory",
                Title = "Continue the Story",
                Purpose = "discover",
                Items = ranked.Select(x => x.Item).ToList(),
                Reasons = reasons,
                // Sits just below Continue Watching / New Since when warm; still valuable when cold.
                WarmPriority = 3,
                ColdPriority = 6,
            },
        };
    }

    private static string ReasonFor(SeriesUserState state, int? gapSeason)
        => state == SeriesUserState.SkippedBacklog && gapSeason is { } s
            ? string.Format(CultureInfo.InvariantCulture, "You skipped Season {0} — start it to complete the story", s)
            : "Pick up where you left off";

    private sealed record Continuity(Guid SeriesId, SeriesStateResult Result, DateTime LastActivityUtc);
}
