using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;

namespace Wholphin.Engine.Behavior;

/// <summary>
/// Hosted service that turns Jellyfin playback and user-data events into Wholphin behavior
/// signals, and runs the persistence consumer for the behavior log.
/// </summary>
public class BehaviorEntryPoint : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly IUserDataManager _userData;
    private readonly IBehaviorService _behavior;
    private readonly ILogger<BehaviorEntryPoint> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumer;

    /// <summary>
    /// Initializes a new instance of the <see cref="BehaviorEntryPoint"/> class.
    /// </summary>
    /// <param name="sessions">Jellyfin session manager (playback events).</param>
    /// <param name="userData">Jellyfin user-data manager (favorite/rating/played events).</param>
    /// <param name="behavior">The behavior capture service.</param>
    /// <param name="logger">The logger.</param>
    public BehaviorEntryPoint(
        ISessionManager sessions,
        IUserDataManager userData,
        IBehaviorService behavior,
        ILogger<BehaviorEntryPoint> logger)
    {
        _sessions = sessions;
        _userData = userData;
        _behavior = behavior;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlaybackStart;
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _userData.UserDataSaved += OnUserDataSaved;

        _consumer = Task.Run(() => _behavior.RunConsumerAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Orca Engine: behavior capture started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlaybackStart;
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        _userData.UserDataSaved -= OnUserDataSaved;

        _cts.Cancel();
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (!IsRelevant(e.Item))
        {
            return;
        }

        var context = BuildContext(e.DeviceName, e.ClientName, null);
        foreach (var user in e.Users)
        {
            _behavior.Record(new BehaviorEvent
            {
                UserId = user.Id,
                JellyfinItemId = e.Item.Id,
                EventType = BehaviorEventType.PlaybackStarted,
                Value = 0,
                ContextJson = context
            });
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (!IsRelevant(e.Item))
        {
            return;
        }

        var completion = Completion(e);
        var finished = e.PlayedToCompletion || completion >= SignalWeights.NearCompleteThreshold;
        var type = finished ? BehaviorEventType.PlaybackCompleted : BehaviorEventType.PlaybackStopped;
        var context = BuildContext(e.DeviceName, e.ClientName, completion);

        foreach (var user in e.Users)
        {
            _behavior.Record(new BehaviorEvent
            {
                UserId = user.Id,
                JellyfinItemId = e.Item.Id,
                EventType = type,
                Value = finished ? 1.0 : completion,
                ContextJson = context
            });
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (!IsRelevant(e.Item) || e.UserData is null)
        {
            return;
        }

        // Only map deliberate user actions; playback-driven saves arrive via the session events above.
        BehaviorEventType type;
        double value;
        switch (e.SaveReason)
        {
            case UserDataSaveReason.TogglePlayed:
                type = e.UserData.Played ? BehaviorEventType.MarkedPlayed : BehaviorEventType.MarkedUnplayed;
                value = e.UserData.Played ? 1 : 0;
                break;
            case UserDataSaveReason.UpdateUserRating:
                type = BehaviorEventType.Rated;
                value = e.UserData.Rating ?? 0;
                break;
            case UserDataSaveReason.UpdateUserData:
                type = e.UserData.IsFavorite ? BehaviorEventType.MarkedFavorite : BehaviorEventType.UnmarkedFavorite;
                value = e.UserData.IsFavorite ? 1 : 0;
                break;
            default:
                return; // PlaybackStart/Progress/Finished/Import — ignored here
        }

        _behavior.Record(new BehaviorEvent
        {
            UserId = e.UserId,
            JellyfinItemId = e.Item.Id,
            EventType = type,
            Value = value,
            ContextJson = BuildContext(null, null, null)
        });
    }

    private static bool IsRelevant(BaseItem? item)
    {
        if (item is null)
        {
            return false;
        }

        var kind = item.GetBaseItemKind();
        return kind is BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.Episode;
    }

    private static double Completion(PlaybackProgressEventArgs e)
    {
        var runtime = e.Item?.RunTimeTicks ?? 0;
        var position = e.PlaybackPositionTicks ?? 0;
        if (runtime <= 0)
        {
            return 0;
        }

        var fraction = (double)position / runtime;
        return Math.Clamp(fraction, 0, 1);
    }

    private static string BuildContext(string? device, string? client, double? completion)
    {
        // Capture the household-local hour so a signal is scored under the daypart it was learned in
        // (selection uses the same zone via Daypart.Current()).
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Personalization.HouseholdClock.Resolve());
        var ctx = new Dictionary<string, object?>
        {
            ["hour"] = local.Hour,
            ["dow"] = (int)local.DayOfWeek
        };

        if (!string.IsNullOrEmpty(device))
        {
            ctx["device"] = device;
        }

        if (!string.IsNullOrEmpty(client))
        {
            ctx["client"] = client;
        }

        if (completion is { } c)
        {
            ctx["completion"] = Math.Round(c, 3);
        }

        return JsonSerializer.Serialize(ctx);
    }
}
