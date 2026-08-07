using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wholphin.Engine.Http;

/// <summary>
/// Runs one of the engine's external binaries (yt-dlp, ffmpeg, ffprobe) with a hard timeout.
///
/// Extracted from the trailer pipeline once torrent streaming needed ffprobe too — the timeout and
/// kill-on-hang handling is exactly the sort of thing that drifts apart when it exists in two places.
/// Every method fails soft: a missing binary or a hung process yields null/false, never an exception,
/// because none of the callers are worth failing a request over.
/// </summary>
public static class ExternalProcess
{
    /// <summary>Runs a binary and returns its stdout, or null on non-zero exit / timeout / spawn failure.</summary>
    /// <param name="fileName">Executable name, resolved on PATH.</param>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="timeoutMs">Hard timeout; the process is killed past it.</param>
    /// <param name="logger">Logger for spawn failures.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>stdout, or null.</returns>
    public static async Task<string?> CaptureAsync(
        string fileName,
        string args,
        int timeoutMs,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            p.Start();

            // Started before waiting: a process that fills the stdout pipe blocks forever if nobody
            // is draining it, and the timeout below would never be reached.
            var stdout = p.StandardOutput.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(p);
                return null;
            }

            return p.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Orca Engine: capture process {File} failed.", fileName);
            return null;
        }
    }

    /// <summary>Kills a process and its children, swallowing the race where it already exited.</summary>
    /// <param name="p">The process.</param>
    public static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone, or we lost the right to signal it. Either way there is nothing to do.
        }
    }
}
