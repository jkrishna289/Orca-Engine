using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Wholphin.Engine.Diagnostics;

namespace Wholphin.Engine.Home;

/// <summary>
/// The fail-soft policy every home row is built under: bound it in time, and on a blown budget or a
/// throw yield nothing rather than let one row stall or fail the whole page. Rows are built
/// concurrently, so this is what makes the home cost the SLOWEST row instead of the sum of them.
/// </summary>
/// <remarks>
/// Kept out of <see cref="HomeService"/> so the one piece of genuinely branching logic here is
/// testable on its own, without standing up the service's full dependency graph.
/// </remarks>
public static class RowBudget
{
    /// <summary>
    /// Runs one row producer under its own time budget.
    /// </summary>
    /// <typeparam name="T">The producer's result type.</typeparam>
    /// <param name="metrics">Counter sink for timeouts/errors/timing.</param>
    /// <param name="name">Row name, used as the metric namespace (<c>home.row.{name}.*</c>).</param>
    /// <param name="budget">How long the producer may take before it's abandoned.</param>
    /// <param name="build">The producer.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// The producer's result, or null when the budget expired or it threw. Only the CALLER's
    /// cancellation — the client gave up on the whole page — propagates.
    /// </returns>
    /// <remarks>
    /// Every row producer routes through here, so this is also the one place that has to know how
    /// long a row took. It emits <c>home.row.{name}.count</c> + <c>home.row.{name}.total_ms</c> —
    /// the same count/total pair <c>personalization.recompute.*</c> uses, so
    /// <c>GET /OrcaEngine/Admin/Diagnostics</c> can average them the way it already averages that.
    /// Timeouts and errors are timed too: a row that always burns its full budget before failing is
    /// exactly the one worth finding.
    /// </remarks>
    public static async Task<T?> OrDefaultAsync<T>(
        IEngineMetrics metrics,
        string name,
        TimeSpan budget,
        Func<CancellationToken, Task<T?>> build,
        CancellationToken ct)
        where T : class
    {
        using var budgeted = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgeted.CancelAfter(budget);
        var startedAt = Stopwatch.GetTimestamp();
        var detail = (string?)null;
        Exception? failure = null;
        try
        {
            return await build(budgeted.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            metrics.Increment($"home.row.{name}.timeout");
            detail = $"budget {budget.TotalMilliseconds:F0}ms exceeded";
            return null;
        }
        catch (OperationCanceledException)
        {
            // The client disconnected — abandoning the whole build is the right answer.
            throw;
        }
        catch (Exception ex)
        {
            metrics.Increment($"home.row.{name}.error");
            failure = ex;
            return null;
        }
        finally
        {
            // A build the CALLER cancelled was abandoned mid-flight; timing it would report a
            // truncated duration as if it were the row's real cost.
            if (!ct.IsCancellationRequested)
            {
                metrics.Record(
                    $"home.row.{name}",
                    ElapsedMs(startedAt),
                    ok: detail is null && failure is null,
                    data: detail,
                    exception: failure);
            }
        }
    }

    /// <summary>Whole milliseconds elapsed since a <see cref="Stopwatch.GetTimestamp"/> reading.</summary>
    /// <param name="startedAt">The starting timestamp.</param>
    /// <returns>Elapsed whole milliseconds.</returns>
    internal static long ElapsedMs(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    /// <summary>
    /// Runs an optional enhancement step (an LLM curation) under a tighter budget, degrading to a
    /// fallback the caller can already ship. Only the timeout is caught: the LLM re-ranker already
    /// passes the input order through on every other kind of miss.
    /// </summary>
    /// <typeparam name="T">The step's result type.</typeparam>
    /// <param name="metrics">Counter sink for timeouts.</param>
    /// <param name="name">Metric namespace (<c>home.{name}.timeout</c>).</param>
    /// <param name="budget">How long the step may take.</param>
    /// <param name="step">The enhancement step.</param>
    /// <param name="fallback">What to use when it's too slow.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The step's result, or <paramref name="fallback"/> when it blew its budget.</returns>
    public static async Task<T> OrFallbackAsync<T>(
        IEngineMetrics metrics,
        string name,
        TimeSpan budget,
        Func<CancellationToken, Task<T>> step,
        T fallback,
        CancellationToken ct)
    {
        using var budgeted = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgeted.CancelAfter(budget);
        var startedAt = Stopwatch.GetTimestamp();
        var detail = (string?)null;
        try
        {
            return await step(budgeted.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            metrics.Increment($"home.{name}.timeout");
            detail = $"budget {budget.TotalMilliseconds:F0}ms exceeded, fell back";
            return fallback;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                metrics.Record($"home.{name}", ElapsedMs(startedAt), ok: detail is null, data: detail);
            }
        }
    }
}
