using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Wholphin.Engine.Behavior;

/// <summary>
/// Jellyfin scheduled task that re-reads every account's watch history and folds anything new into
/// their taste profile. Appears under Dashboard → Scheduled Tasks, runs daily by default, and the
/// trigger is fully editable there or can be run on demand.
/// </summary>
/// <remarks>
/// <para>
/// Live capture already sees ordinary viewing, so this is reconciliation rather than the main
/// pipeline. It catches what live capture structurally cannot: anything watched while the server or
/// the plugin was down, bulk "mark as played" edits, accounts added after the last manual scan, and
/// ratings or favourites changed by a client that did not raise an event the engine saw.
/// </para>
/// <para>
/// Cheap to repeat because the import is idempotent — each run replaces its own previous rows rather
/// than appending — so a daily schedule cannot inflate anyone's history.
/// </para>
/// </remarks>
public class ImportWatchHistoryTask : IScheduledTask
{
    /// <summary>Default interval between runs. Reconciliation, so daily is generous.</summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(24);

    private readonly IWatchHistoryImporter _importer;

    /// <summary>Initializes a new instance of the <see cref="ImportWatchHistoryTask"/> class.</summary>
    /// <param name="importer">The watch-history importer.</param>
    public ImportWatchHistoryTask(IWatchHistoryImporter importer) => _importer = importer;

    /// <inheritdoc />
    public string Name => "Import Jellyfin watch history";

    /// <inheritdoc />
    public string Key => "OrcaEngineImportWatchHistory";

    /// <inheritdoc />
    public string Description =>
        "Re-reads every account's played, favourite and rating state from Jellyfin and folds anything "
        + "new into their taste profile. Safe to repeat; each run replaces its own previous rows.";

    /// <inheritdoc />
    public string Category => "Orca Engine";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        // Awaited rather than fired off, or Jellyfin would mark the task complete the instant it
        // started and show a duration of zero for work that takes minutes. A manual scan already
        // running simply wins; this run stands down rather than queueing behind it.
        await _importer.RunAsync(cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = DefaultInterval.Ticks,
            },
        };
}
