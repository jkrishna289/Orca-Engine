using System;
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
    /// <param name="metrics">Counter sink for timeouts/errors.</param>
    /// <param name="name">Row name, used as the metric namespace (<c>home.row.{name}.*</c>).</param>
    /// <param name="budget">How long the producer may take before it's abandoned.</param>
    /// <param name="build">The producer.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// The producer's result, or null when the budget expired or it threw. Only the CALLER's
    /// cancellation — the client gave up on the whole page — propagates.
    /// </returns>
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
        try
        {
            return await build(budgeted.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            metrics.Increment($"home.row.{name}.timeout");
            return null;
        }
        catch (OperationCanceledException)
        {
            // The client disconnected — abandoning the whole build is the right answer.
            throw;
        }
        catch (Exception)
        {
            metrics.Increment($"home.row.{name}.error");
            return null;
        }
    }

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
        try
        {
            return await step(budgeted.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            metrics.Increment($"home.{name}.timeout");
            return fallback;
        }
    }
}
