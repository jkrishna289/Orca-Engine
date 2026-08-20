using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Data;
using Wholphin.Engine.Personalization;
using Wholphin.Engine.Recommendation;

namespace Wholphin.Engine.Controllers;

/// <summary>
/// One answer to the question the dashboard could not previously answer: has this household's taste
/// actually been learned, is it being learned right now, or has nothing happened yet.
/// </summary>
/// <remarks>
/// The information existed — spread across a behavior-event count, an affinity row, a profile file
/// on disk and an in-memory vector index — and no screen put it together, so the only way to know
/// was to read a JSON file over SSH. This endpoint is deliberately one call that returns a state per
/// user, because "is it built?" is one question.
/// </remarks>
[ApiController]
[Route("OrcaEngine/TasteGraph")]
[Produces("application/json")]
[Authorize(Policy = "RequiresElevation")]
public class TasteGraphController : ControllerBase
{
    private readonly IUserManager _users;
    private readonly IWholphinDbContextFactory _factory;
    private readonly ITasteProfileService _tasteProfiles;
    private readonly IWatchHistoryImporter _importer;
    private readonly IContentVectorIndex _index;

    /// <summary>Initializes a new instance of the <see cref="TasteGraphController"/> class.</summary>
    /// <param name="users">Jellyfin user manager (enumerates accounts).</param>
    /// <param name="factory">Database context factory.</param>
    /// <param name="tasteProfiles">The taste-profile file service.</param>
    /// <param name="importer">The watch-history importer (is a scan running?).</param>
    /// <param name="index">The content-vector index.</param>
    public TasteGraphController(
        IUserManager users,
        IWholphinDbContextFactory factory,
        ITasteProfileService tasteProfiles,
        IWatchHistoryImporter importer,
        IContentVectorIndex index)
    {
        _users = users;
        _factory = factory;
        _tasteProfiles = tasteProfiles;
        _importer = importer;
        _index = index;
    }

    /// <summary>Returns the taste-graph state for every Jellyfin account.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-user state plus the shared vector-index state.</returns>
    [HttpGet]
    public async Task<ActionResult> Get(CancellationToken cancellationToken)
    {
        var progress = _importer.Progress;
        var runningFor = progress.Running
            ? progress.Users.Where(u => u.State == "running").Select(u => u.UserId).ToHashSet()
            : new HashSet<Guid>();

        await using var db = _factory.Create();

        // Two grouped counts rather than a query per user: a household is small, but the difference
        // between "captured live" and "imported" is the whole point of the screen.
        var totals = await db.BehaviorEvents.AsNoTracking()
            .GroupBy(e => e.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Total = g.Count(),
                Imported = g.Count(e => e.ContextJson == WatchHistoryImporter.ImportContext),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byUser = totals.ToDictionary(t => t.UserId, t => t);

        var users = new List<object>();
        foreach (var id in JellyfinUsers.AllIds(_users))
        {
            var user = _users.GetUserById(id);
            if (user is null)
            {
                continue;
            }

            var counts = byUser.GetValueOrDefault(user.Id);
            var events = counts?.Total ?? 0;
            var imported = counts?.Imported ?? 0;
            var profile = await _tasteProfiles.GetAsync(user.Id, cancellationToken).ConfigureAwait(false);

            users.Add(new
            {
                UserId = user.Id,
                UserName = string.IsNullOrWhiteSpace(user.Username) ? user.Id.ToString("N") : user.Username,
                State = StateOf(runningFor.Contains(user.Id), events, profile),
                Events = events,
                ImportedEvents = imported,
                LiveEvents = events - imported,
                Seeds = profile?.Seeds.Count ?? 0,
                Confidence = profile is null ? 0 : Math.Round(profile.Confidence, 3),
                profile?.TopGenres,
                profile?.TopLanguages,
                profile?.TopKeywords,
                HasVector = profile?.DenseVector is { Length: > 0 },
                BuiltAtUtc = profile?.GeneratedAt,
                Provider = profile?.Provider,
            });
        }

        var snapshot = _index.Current;

        return Ok(new
        {
            Import = new
            {
                progress.Running,
                progress.Phase,
                progress.UsersDone,
                progress.UsersTotal,
                progress.EventsImported,
                progress.StartedUtc,
                progress.FinishedUtc,
            },
            VectorIndex = snapshot is null
                ? null
                : new { snapshot.Count, snapshot.ProviderName, snapshot.BuiltAtUtc },
            Users = users,
        });
    }

    /// <summary>
    /// The single word the screen leads with, in the order an operator actually needs to rule out.
    /// </summary>
    private static string StateOf(bool importing, int events, UserTasteProfile? profile)
    {
        if (importing)
        {
            return "building";
        }

        if (events == 0)
        {
            return "empty";
        }

        if (profile is null || profile.Seeds.Count == 0)
        {
            // Events exist but nothing has folded them into a profile yet — the window between an
            // import finishing and the recompute worker catching up.
            return "pending";
        }

        return profile.Confidence >= WatchHistoryImporter.TargetConfidence ? "ready" : "thin";
    }
}
