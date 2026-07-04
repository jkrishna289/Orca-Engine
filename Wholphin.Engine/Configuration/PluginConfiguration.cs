using MediaBrowser.Model.Plugins;

namespace Wholphin.Engine.Configuration;

/// <summary>
/// Admin-editable plugin configuration, surfaced on the Jellyfin dashboard. This is the
/// <b>admin layer</b> of the layered settings chain (see <see cref="Wholphin.Engine.Settings.SettingsService"/>):
/// it overrides system defaults and is in turn overridable per-user by the roaming settings store.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the engine is enabled (global kill-switch).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how often (in minutes) precomputed read-models
    /// (home, recommendations) are refreshed.
    /// </summary>
    public int RefreshIntervalMinutes { get; set; } = 60;

    /// <summary>Gets or sets the default items-per-row when the client does not specify one.</summary>
    public int DefaultRowSize { get; set; } = 20;

    /// <summary>Gets or sets the number of items in the rotating spotlight billboard.</summary>
    public int SpotlightCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets how many days of raw behavior events to retain (older ones are pruned; the
    /// affinity vector already decays them to near-zero). Bounds recompute cost as history grows.
    /// 0 = keep everything (no pruning).
    /// </summary>
    public int BehaviorRetentionDays { get; set; } = 400;

    /// <summary>
    /// Gets or sets the household IANA/Windows time-zone id used for daypart bucketing (morning/
    /// evening/…). Empty = the server's local time zone. Applied consistently to signal capture and
    /// selection so a signal is scored under the daypart it was learned in.
    /// </summary>
    public string HouseholdTimeZone { get; set; } = string.Empty;

    // --- Feature flags (admin layer) -------------------------------------------------------

    /// <summary>Gets or sets a value indicating whether personalized recommendations are produced.</summary>
    public bool FeaturePersonalization { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the spotlight (Hero billboard) row is emitted.</summary>
    public bool FeatureSpotlight { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Continue Watching" row is emitted.</summary>
    public bool FeatureContinueWatching { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Trending" row is emitted.</summary>
    public bool FeatureTrending { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether content-similarity rows ("Because You Watched X") are emitted.</summary>
    public bool FeatureSimilarityRows { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the recommender injects controlled exploration (novel picks).</summary>
    public bool FeatureExploration { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "New Since You Were Away" row is emitted. (Milestone 8.)</summary>
    public bool FeatureNewSinceAway { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Coming Soon" calendar row is emitted. (Milestone 8.)</summary>
    public bool FeatureComingSoon { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether mood-based collection rows are emitted. (Milestone 8.)</summary>
    public bool FeatureMoodCollections { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the server pre-buffers trailers (needs yt-dlp/ffmpeg). (Milestone 8.)</summary>
    public bool FeatureTrailerPrebuffer { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Groq-generated content advisories are produced and shown in the player. Needs a Groq key; independent of the LLM re-ranker.</summary>
    public bool FeatureContentWarnings { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether engine-proxied requests are accepted. (Milestone 2.)</summary>
    public bool FeatureRequests { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether taste-driven discovery is active: per-user
    /// justified TMDB pulls (You Might Like / Because You Watched) plus the global trending and
    /// per-country rows. Needs a TMDB key. Replaces the old indiscriminate discovery import.
    /// </summary>
    public bool FeatureTasteDiscovery { get; set; }

    // --- Discovery tuning (admin-only; defaults are the designed behavior) ------------------

    /// <summary>Gets or sets the profile confidence (0-1) below which per-user taste pulls are skipped.</summary>
    public double DiscoveryMinConfidence { get; set; } = 0.40;

    /// <summary>Gets or sets the maximum new catalog rows one per-user pull may import.</summary>
    public int DiscoveryMaxImportsPerPull { get; set; } = 25;

    /// <summary>Gets or sets the taste-pick lifetime in days (seeded picks live twice as long).</summary>
    public int DiscoveryPickTtlDays { get; set; } = 7;

    /// <summary>Gets or sets how many users get a taste pull per discovery cycle (rotation).</summary>
    public int DiscoveryMaxUsersPerCycle { get; set; } = 10;

    /// <summary>Gets or sets the share (0.05-0.15) of taste picks reserved for labeled exploration.</summary>
    public double DiscoveryExplorationFraction { get; set; } = 0.10;

    /// <summary>Gets or sets how much the viewer's taste flavors the trending row (0 = pure popularity).</summary>
    public double DiscoveryTrendingTasteWeight { get; set; } = 0.30;

    // --- Ranking weights (advanced; resolved via ScoringPolicy, defaults = blueprint constants) ---

    /// <summary>Gets or sets the "For You" personalization weight (0-1). Default 0.60.</summary>
    public double WeightRecPersonalization { get; set; } = 0.60;

    /// <summary>Gets or sets the "For You" quality weight (0-1). Default 0.15.</summary>
    public double WeightRecQuality { get; set; } = 0.15;

    /// <summary>Gets or sets the "For You" recency weight (0-1). Default 0.05.</summary>
    public double WeightRecRecency { get; set; } = 0.05;

    /// <summary>Gets or sets the "For You" availability weight (0-1). Default 0.10.</summary>
    public double WeightRecAvailability { get; set; } = 0.10;

    /// <summary>Gets or sets the discovery taste weight (0-1). Default 0.55.</summary>
    public double WeightDiscTaste { get; set; } = 0.55;

    /// <summary>Gets or sets the discovery popularity weight (0-1). Default 0.20.</summary>
    public double WeightDiscPopularity { get; set; } = 0.20;

    /// <summary>Gets or sets the discovery freshness weight (0-1). Default 0.10.</summary>
    public double WeightDiscFreshness { get; set; } = 0.10;

    /// <summary>Gets or sets the discovery novelty weight (0-1). Default 0.10.</summary>
    public double WeightDiscNovelty { get; set; } = 0.10;

    /// <summary>Gets or sets the discovery source-confidence weight (0-1). Default 0.05.</summary>
    public double WeightDiscSourceConfidence { get; set; } = 0.05;

    // --- Milestone 2 integrations ----------------------------------------------------------

    /// <summary>Gets or sets the Jellyseerr base URL (e.g., http://localhost:5055). Empty disables the integration.</summary>
    public string JellyseerrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyseerr API key (sent as the X-Api-Key header).</summary>
    public string JellyseerrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB API key (v3) for direct discovery. Optional — Jellyseerr can proxy discovery instead.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ISO-3166-1 country code (e.g., "US", "IN") used to resolve the streaming-provider
    /// brand shown as the studio card tag, from TMDB watch providers. Needs a TMDB key.
    /// </summary>
    public string WatchProviderRegion { get; set; } = "US";

    /// <summary>Gets or sets the Sonarr base URL (e.g., http://localhost:8989). Empty disables the Sonarr calendar. (Milestone 8.)</summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Sonarr API key (sent as the X-Api-Key header).</summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Radarr base URL (e.g., http://localhost:7878). Empty disables the Radarr calendar. (Milestone 8.)</summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Radarr API key (sent as the X-Api-Key header).</summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    // --- Stage-3 LLM re-ranker (Groq; opt-in, additive) -----------------------------------

    /// <summary>
    /// Gets or sets a value indicating whether the hosted-LLM re-ranker is active. Additive only:
    /// when on (and a Groq key is set) the personalized "For You" row is reordered + annotated by
    /// the LLM; otherwise the engine's local ranking is used unchanged. Off by default.
    /// </summary>
    public bool FeatureLlmRerank { get; set; }

    /// <summary>Gets or sets the Groq API key (Bearer auth). Empty disables the LLM re-ranker.</summary>
    public string GroqApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Groq chat model id used for re-ranking (OpenAI-compatible).</summary>
    public string GroqModel { get; set; } = "llama-3.3-70b-versatile";

    // --- Embeddings (pluggable content vectors; default local TF-IDF) ----------------------

    /// <summary>
    /// Gets or sets the active embedding provider: <c>tfidf</c> (default, local), <c>openai</c>,
    /// <c>gemini</c>, <c>voyage</c>, <c>jina</c>, or <c>onnx</c>. Falls back to TF-IDF when the chosen
    /// provider is unconfigured or fails.
    /// </summary>
    public string EmbeddingProvider { get; set; } = "tfidf";

    /// <summary>Gets or sets the OpenAI API key (for the OpenAI embedding provider).</summary>
    public string OpenAiApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the OpenAI embedding model id.</summary>
    public string OpenAiEmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary>Gets or sets the Google Gemini API key (for the Gemini embedding provider).</summary>
    public string GeminiApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Gemini embedding model id.</summary>
    public string GeminiEmbeddingModel { get; set; } = "gemini-embedding-001";

    /// <summary>Gets or sets the Voyage AI API key (for the Voyage embedding provider).</summary>
    public string VoyageApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Voyage embedding model id.</summary>
    public string VoyageEmbeddingModel { get; set; } = "voyage-3.5";

    /// <summary>Gets or sets the Jina API key (for the Jina embedding provider).</summary>
    public string JinaApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jina embedding model id.</summary>
    public string JinaEmbeddingModel { get; set; } = "jina-embeddings-v3";

    /// <summary>Gets or sets the filesystem path to a local ONNX embedding model (enables the ONNX provider).</summary>
    public string OnnxModelPath { get; set; } = string.Empty;
}
