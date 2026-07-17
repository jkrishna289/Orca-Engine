using System;
using Wholphin.Engine.Configuration;

namespace Wholphin.Engine.Discovery;

/// <summary>
/// The admin-tunable discovery knobs, resolved from <see cref="PluginConfiguration"/> with the
/// designed defaults and safety clamps. Loaded once per run by the orchestrator and carried on
/// the context; only knobs with user-visible behavior are exposed — scoring blend weights stay
/// code constants until run data justifies tuning them.
/// </summary>
public class DiscoveryTuning
{
    /// <summary>Gets or sets the profile confidence below which per-user pulls are skipped (cold start).</summary>
    public double MinConfidence { get; set; } = 0.40;

    /// <summary>Gets or sets the maximum NEW catalog rows one per-user pull may import.</summary>
    public int MaxImportsPerPull { get; set; } = 25;

    /// <summary>Gets or sets the taste-pick TTL in days (Because You Watched lives twice as long; trending/country 3 days).</summary>
    public int PickTtlDays { get; set; } = 7;

    /// <summary>Gets or sets how many users get a taste pull per discovery cycle (round-robin rotation).</summary>
    public int MaxUsersPerCycle { get; set; } = 10;

    /// <summary>Gets or sets the share of taste-match slots reserved for exploration picks (clamped 0.05–0.15).</summary>
    public double ExplorationFraction { get; set; } = 0.10;

    /// <summary>Gets or sets how much the viewer's taste flavors the trending row (0 = pure popularity).</summary>
    public double TrendingTasteWeight { get; set; } = 0.30;

    /// <summary>Gets or sets how many recommendations each per-media-type LLM discovery call asks for.</summary>
    public int LlmMaxPerMediaType { get; set; } = 10;

    /// <summary>Gets or sets how many watch-history titles are sent per LLM discovery call.</summary>
    public int LlmHistoryCap { get; set; } = 20;

    /// <summary>Resolves the tuning from the live plugin configuration, clamping every knob to a sane range.</summary>
    /// <returns>The resolved tuning.</returns>
    public static DiscoveryTuning Resolve()
    {
        var config = Plugin.Instance?.Configuration;
        var tuning = new DiscoveryTuning();
        if (config is null)
        {
            return tuning;
        }

        tuning.MinConfidence = Clamp(config.DiscoveryMinConfidence, 0.0, 1.0, tuning.MinConfidence);
        tuning.MaxImportsPerPull = (int)Clamp(config.DiscoveryMaxImportsPerPull, 1, 100, tuning.MaxImportsPerPull);
        tuning.PickTtlDays = (int)Clamp(config.DiscoveryPickTtlDays, 1, 60, tuning.PickTtlDays);
        tuning.MaxUsersPerCycle = (int)Clamp(config.DiscoveryMaxUsersPerCycle, 1, 100, tuning.MaxUsersPerCycle);
        tuning.ExplorationFraction = Clamp(config.DiscoveryExplorationFraction, 0.05, 0.15, tuning.ExplorationFraction);
        tuning.TrendingTasteWeight = Clamp(config.DiscoveryTrendingTasteWeight, 0.0, 1.0, tuning.TrendingTasteWeight);
        tuning.LlmMaxPerMediaType = (int)Clamp(config.LlmDiscoveryMaxPerMediaType, 1, 25, tuning.LlmMaxPerMediaType);
        tuning.LlmHistoryCap = (int)Clamp(config.LlmDiscoveryHistoryCap, 5, 40, tuning.LlmHistoryCap);
        return tuning;
    }

    private static double Clamp(double value, double min, double max, double fallback)
        => value < min || value > max ? fallback : value;
}
