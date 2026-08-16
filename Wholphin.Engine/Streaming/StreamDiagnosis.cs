using System;

namespace Wholphin.Engine.Streaming;

/// <summary>What the telemetry says is wrong with a session, and what to do about it.</summary>
/// <param name="Headline">One sentence naming the constraint.</param>
/// <param name="Recommendation">The next action worth taking, or empty when none is warranted.</param>
/// <param name="Severity">ok, warn or bad — drives nothing but the colour.</param>
public readonly record struct StreamDiagnosis(string Headline, string Recommendation, string Severity);

/// <summary>
/// Turns a session's measurements into a plain-language diagnosis.
/// </summary>
/// <remarks>
/// Exists because the raw numbers do not tell an operator which of several very different faults they
/// are looking at. "56 seeds, 8 connections, 874 KB/s against a 908 KB/s requirement" is a complete
/// description of a connection-ramp limit and reads, to almost everyone, as a healthy swarm.
///
/// Every branch below is decidable from the telemetry passed in. Nothing here infers a cause it
/// cannot see: notably, a ramp pinned at the connection ceiling produces a recommendation to *prove
/// inbound reachability*, never to raise the ceiling — raising it is the change that twice took the
/// household network down, and the evidence available here cannot distinguish "needs more outbound
/// connections" from "is unreachable inbound".
/// </remarks>
public static class StreamDiagnostics
{
    /// <summary>Above this share of the connection ceiling, the ramp is treated as pinned rather than busy.</summary>
    private const double PinnedFraction = 0.9;

    /// <summary>
    /// Diagnoses one session.
    /// </summary>
    /// <param name="state">Session state: Preparing, Ready or Failed.</param>
    /// <param name="hasMetadata">Whether the torrent's file list has arrived.</param>
    /// <param name="openConnections">Peers currently connected.</param>
    /// <param name="maxConnections">The configured connection ceiling.</param>
    /// <param name="seeds">Seeds the trackers reported.</param>
    /// <param name="swarmRateBytes">Measured inbound rate from the swarm.</param>
    /// <param name="requiredBytes">Bytes per second the container needs, or 0 when unknown.</param>
    /// <param name="stalls">Stalled reads so far this session.</param>
    /// <param name="reachability">The inbound reachability verdict.</param>
    /// <returns>The diagnosis.</returns>
    public static StreamDiagnosis Diagnose(
        string state,
        bool hasMetadata,
        int openConnections,
        int maxConnections,
        int seeds,
        long swarmRateBytes,
        long requiredBytes,
        int stalls,
        InboundReachability reachability)
    {
        if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return new StreamDiagnosis(
                "This source failed to open.",
                "Pick another source.",
                "bad");
        }

        if (string.Equals(state, "Preparing", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasMetadata)
            {
                // The failure mode measured on 2026-08-16: a source sat here for two minutes with no
                // log line and no timeout, indistinguishable from one about to succeed.
                return new StreamDiagnosis(
                    "Still waiting for the torrent's file list from peers.",
                    openConnections == 0
                        ? "No peers reached yet. Try another source if this persists past a minute or two."
                        : "Try another source if this persists.",
                    "warn");
            }

            return new StreamDiagnosis(
                "Buffering the opening and closing pieces before playback starts.",
                string.Empty,
                "warn");
        }

        // Ready from here on: the questions become throughput ones.
        var pinned = maxConnections > 0 && openConnections >= maxConnections * PinnedFraction;
        var starved = requiredBytes > 0 && swarmRateBytes < requiredBytes;

        if (pinned && starved)
        {
            return new StreamDiagnosis(
                $"Throughput is below what playback needs, and the connection ramp is at its ceiling "
                    + $"({openConnections} of {maxConnections}) with {seeds} seeds reported.",
                reachability == InboundReachability.Reachable
                    ? "Peers can already reach this server, so the ceiling itself is the limit. Raise it only in small steps."
                    : "Verify inbound reachability before raising connection limits — a forwarded port adds peers without adding outbound attempts.",
                "bad");
        }

        if (starved)
        {
            return new StreamDiagnosis(
                seeds <= 2
                    ? $"Swarm throughput is below the playback bitrate, and only {seeds} seed(s) were found."
                    : "Swarm throughput is below the playback bitrate.",
                "Try another source — this one cannot sustain playback.",
                "bad");
        }

        if (pinned)
        {
            return new StreamDiagnosis(
                $"Keeping up, but every connection slot is in use ({openConnections} of {maxConnections}).",
                reachability == InboundReachability.Reachable
                    ? string.Empty
                    : "Inbound reachability is unproven; a forwarded port would add peers for free.",
                "warn");
        }

        if (stalls > 0)
        {
            return new StreamDiagnosis(
                $"Throughput is sufficient now, but {stalls} read(s) stalled earlier.",
                "Watch for further stalls; early ones are normal while the swarm ramps.",
                "warn");
        }

        return new StreamDiagnosis("Playing normally.", string.Empty, "ok");
    }
}
