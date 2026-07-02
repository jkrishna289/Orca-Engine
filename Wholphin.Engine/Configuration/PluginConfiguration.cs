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

    /// <summary>Gets or sets a value indicating whether availability-aware discovery (Jellyseerr/TMDB) is active. (Milestone 2.)</summary>
    public bool FeatureJellyseerrDiscovery { get; set; }

    /// <summary>Gets or sets a value indicating whether TMDB-direct trending/popular discovery is pulled into the catalog. (Milestone 7.)</summary>
    public bool FeatureTmdbDiscovery { get; set; }

    /// <summary>Gets or sets a value indicating whether engine-proxied requests are accepted. (Milestone 2.)</summary>
    public bool FeatureRequests { get; set; }

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
