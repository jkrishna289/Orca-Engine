using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wholphin.Engine.Configuration;

namespace Wholphin.Engine.Settings;

/// <summary>
/// Default <see cref="ISettingsService"/>. Collapses four ordered layers, each overriding the
/// previous:
/// <list type="number">
///   <item><description><b>System</b> — hard defaults (<see cref="EngineSettings"/> constructors).</description></item>
///   <item><description><b>Admin</b> — the dashboard <see cref="PluginConfiguration"/>.</description></item>
///   <item><description><b>Group</b> — reserved seam (no-op today; users belong to no engine group yet).</description></item>
///   <item><description><b>User</b> — the roaming <see cref="IUserSettingsStore"/>, keyed by the conventions below.</description></item>
/// </list>
/// User-override keys: <c>feature.personalization</c>, <c>feature.spotlight</c>,
/// <c>feature.continueWatching</c>, <c>feature.trending</c> (bool); <c>pref.rowSize</c> (int).
/// Milestone-2 feature flags stay admin-only and are not user-overridable.
/// </summary>
public class SettingsService : ISettingsService
{
    /// <summary>Roaming-settings key for a user's personalization opt-out.</summary>
    public const string KeyPersonalization = "feature.personalization";

    /// <summary>Roaming-settings key for a user's spotlight opt-out.</summary>
    public const string KeySpotlight = "feature.spotlight";

    /// <summary>Roaming-settings key for a user's continue-watching opt-out.</summary>
    public const string KeyContinueWatching = "feature.continueWatching";

    /// <summary>Roaming-settings key for a user's trending opt-out.</summary>
    public const string KeyTrending = "feature.trending";

    /// <summary>Roaming-settings key for a user's similarity-rows opt-out.</summary>
    public const string KeySimilarityRows = "feature.similarityRows";

    /// <summary>Roaming-settings key for a user's exploration opt-out.</summary>
    public const string KeyExploration = "feature.exploration";

    /// <summary>Roaming-settings key for a user's taste-discovery opt-out.</summary>
    public const string KeyTasteDiscovery = "feature.tasteDiscovery";

    /// <summary>Roaming-settings key for a user's LLM-discovery opt-out.</summary>
    public const string KeyLlmDiscovery = "feature.llmDiscovery";

    /// <summary>Roaming-settings key for a user's LLM-curation opt-out.</summary>
    public const string KeyLlmCuration = "feature.llmCuration";

    /// <summary>Roaming-settings key for a user's country override (ISO-3166-1, e.g. "IN").</summary>
    public const string KeyCountry = "pref.country";

    /// <summary>Roaming-settings key for a user's preferred row size.</summary>
    public const string KeyRowSize = "pref.rowSize";

    private readonly IUserSettingsStore _userSettings;
    private readonly ILogger<SettingsService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class.
    /// </summary>
    /// <param name="userSettings">The roaming per-user settings store.</param>
    /// <param name="logger">The logger.</param>
    public SettingsService(IUserSettingsStore userSettings, ILogger<SettingsService> logger)
    {
        _userSettings = userSettings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EngineSettings> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        // Layer 1: system defaults.
        var settings = new EngineSettings();

        // Layer 2: admin (dashboard) configuration.
        ApplyAdmin(settings, Plugin.Instance?.Configuration ?? new PluginConfiguration());

        // Layer 3: group overrides — reserved. Engine groups are not modelled yet, so this is a
        // documented no-op seam; when groups arrive, apply them here between admin and user.

        // Layer 4: per-user roaming overrides.
        if (userId is { } uid && uid != Guid.Empty)
        {
            await ApplyUserAsync(settings, uid, ct).ConfigureAwait(false);
        }

        return settings;
    }

    private static void ApplyAdmin(EngineSettings settings, PluginConfiguration config)
    {
        settings.Enabled = config.Enabled;
        settings.DefaultRowSize = Clamp(config.DefaultRowSize, 1, 100, settings.DefaultRowSize);
        settings.SpotlightCount = Clamp(config.SpotlightCount, 1, 20, settings.SpotlightCount);
        settings.SpotlightShowcaseCount = Clamp(config.SpotlightShowcaseCount, 0, 5, settings.SpotlightShowcaseCount);

        var f = settings.Features;
        f.Personalization = config.FeaturePersonalization;
        f.Spotlight = config.FeatureSpotlight;
        f.SpotlightShowcase = config.FeatureSpotlightShowcase;
        f.ContinueWatching = config.FeatureContinueWatching;
        f.Trending = config.FeatureTrending;
        f.SimilarityRows = config.FeatureSimilarityRows;
        f.Exploration = config.FeatureExploration;
        f.NewSinceAway = config.FeatureNewSinceAway;
        f.ComingSoon = config.FeatureComingSoon;
        f.ContinueTheStory = config.FeatureContinueTheStory;
        f.MoodCollections = config.FeatureMoodCollections;
        f.TrailerPrebuffer = config.FeatureTrailerPrebuffer;
        f.Requests = config.FeatureRequests;
        f.TasteDiscovery = config.FeatureTasteDiscovery;
        f.LlmDiscovery = config.FeatureLlmDiscovery;
        f.LlmCuration = config.FeatureLlmCuration;

        if (IsCountry(config.WatchProviderRegion))
        {
            settings.CountryCode = config.WatchProviderRegion.Trim().ToUpperInvariant();
        }
    }

    private async Task ApplyUserAsync(EngineSettings settings, Guid userId, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> overrides;
        try
        {
            overrides = await _userSettings.GetAllAsync(userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Roaming overrides are best-effort; never let a read failure break settings resolution.
            _logger.LogWarning(ex, "Orca Engine: failed to load roaming settings for {UserId}.", userId);
            return;
        }

        var f = settings.Features;
        f.Personalization = Bool(overrides, KeyPersonalization, f.Personalization);
        f.Spotlight = Bool(overrides, KeySpotlight, f.Spotlight);
        f.ContinueWatching = Bool(overrides, KeyContinueWatching, f.ContinueWatching);
        f.Trending = Bool(overrides, KeyTrending, f.Trending);
        f.SimilarityRows = Bool(overrides, KeySimilarityRows, f.SimilarityRows);
        f.Exploration = Bool(overrides, KeyExploration, f.Exploration);
        f.TasteDiscovery = Bool(overrides, KeyTasteDiscovery, f.TasteDiscovery);
        f.LlmDiscovery = Bool(overrides, KeyLlmDiscovery, f.LlmDiscovery);
        f.LlmCuration = Bool(overrides, KeyLlmCuration, f.LlmCuration);
        settings.DefaultRowSize = Clamp(Int(overrides, KeyRowSize, settings.DefaultRowSize), 1, 100, settings.DefaultRowSize);

        if (overrides.TryGetValue(KeyCountry, out var country))
        {
            var cc = country.Trim().Trim('"').ToUpperInvariant();
            if (IsCountry(cc))
            {
                settings.CountryCode = cc;
            }
        }
    }

    private static bool IsCountry(string? value)
    {
        var cc = value?.Trim().Trim('"');
        if (cc is not { Length: 2 })
        {
            return false;
        }

        return char.IsAsciiLetter(cc[0]) && char.IsAsciiLetter(cc[1]);
    }

    private static bool Bool(IReadOnlyDictionary<string, string> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var v = raw.Trim().Trim('"');
        if (bool.TryParse(v, out var parsed))
        {
            return parsed;
        }

        return v switch
        {
            "1" => true,
            "0" => false,
            _ => fallback,
        };
    }

    private static int Int(IReadOnlyDictionary<string, string> map, string key, int fallback)
    {
        if (!map.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return int.TryParse(raw.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value < min || value > max)
        {
            return Math.Clamp(fallback, min, max);
        }

        return value;
    }
}
