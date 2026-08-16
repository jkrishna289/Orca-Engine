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
    /// Gets or sets how many cinematic Spotlight showcase rows a home may contain. Each holds a
    /// single item and is placed deep in the feed. Distinct from <see cref="SpotlightCount"/>,
    /// which sizes the top billboard. 0 disables them.
    /// </summary>
    public int SpotlightShowcaseCount { get; set; } = 2;

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

    /// <summary>Gets or sets a value indicating whether the cinematic Spotlight showcase rows are emitted.</summary>
    public bool FeatureSpotlightShowcase { get; set; } = true;

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

    /// <summary>Gets or sets a value indicating whether the "Continue the Story" row (skipped seasons + abandoned series) is emitted.</summary>
    public bool FeatureContinueTheStory { get; set; } = true;

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

    /// <summary>
    /// Gets or sets the preferred trailer audio language (ISO 639-1, e.g. "en", "hi") used when
    /// picking a title's trailer from TMDB's videos: a matching-language trailer wins whenever one
    /// exists, falling back to the title's native trailer otherwise. Clients may override per
    /// request via the trailer endpoints' <c>lang</c> parameter.
    /// </summary>
    public string TrailerLanguage { get; set; } = "en";

    // --- Trailer sources -------------------------------------------------------------------

    /// <summary>
    /// Gets or sets the ordered, comma-separated list of trailer sources to try. Recognised tokens:
    /// <c>tmdb</c> (TMDB's videos, language-aware), <c>jellyfin</c> (the trailer URLs Jellyfin's own
    /// metadata providers already found for a library item), <c>stored</c> (the URL saved on the
    /// catalog row), and <c>search</c> (ask yt-dlp to search YouTube by title and year).
    /// </summary>
    /// <remarks>
    /// Removing a token disables that source; reordering changes preference. Unrecognised tokens are
    /// ignored, so a typo costs a source rather than breaking resolution. <c>search</c> is off by
    /// default deliberately — a text search can return a fan edit or a review rather than the real
    /// trailer, which is worse than showing none.
    /// </remarks>
    public string TrailerSourceOrder { get; set; } = "tmdb,jellyfin,stored";

    /// <summary>
    /// Gets or sets a value indicating whether a teaser may be used when no full trailer exists.
    /// </summary>
    public bool TrailerAllowTeasers { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether only videos TMDB marks "official" are eligible.
    /// Official is always preferred; this makes it mandatory.
    /// </summary>
    public bool TrailerRequireOfficial { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether non-YouTube videos (Vimeo, …) may be used. yt-dlp can
    /// fetch them, but they are rarer and less consistently the real trailer.
    /// </summary>
    public bool TrailerAllowNonYouTube { get; set; }

    /// <summary>Gets or sets the Sonarr base URL (e.g., http://localhost:8989). Empty disables the Sonarr calendar. (Milestone 8.)</summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Sonarr API key (sent as the X-Api-Key header).</summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Radarr base URL (e.g., http://localhost:7878). Empty disables the Radarr calendar. (Milestone 8.)</summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Radarr API key (sent as the X-Api-Key header).</summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    // --- Torrent source streaming (opt-in; off by default) ---------------------------------

    /// <summary>
    /// Gets or sets a value indicating whether torrent source streaming is active. Off by default:
    /// peer traffic originates from this server's IP, so enabling it is an operator decision and
    /// deliberately not a silent upgrade. With this off the endpoints 404 and the app hides the
    /// feature entirely.
    /// </summary>
    public bool FeatureSourceStreaming { get; set; }

    /// <summary>
    /// Gets or sets the directory holding in-flight torrent pieces. Empty uses an "orca-streams"
    /// folder under the plugin's data path. Needs room for whole media files while streaming.
    /// </summary>
    public string StreamCachePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the cap in GB for the piece cache; the oldest finished session is evicted first.</summary>
    public int StreamCacheMaxGb { get; set; } = 20;

    /// <summary>
    /// Gets or sets the folder a kept title is copied into, which must be inside a Jellyfin library
    /// for the scan to find it.
    /// </summary>
    /// <remarks>
    /// Empty disables keeping, and that is the only safe default. Picking a folder on the operator's
    /// behalf would either land films somewhere Jellyfin never scans — reporting success and producing
    /// nothing — or start writing into a library the operator never agreed to have written to.
    /// </remarks>
    public string StreamLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many torrent sessions may run at once. Each costs disk, bandwidth and peer
    /// connections, and MonoTorrent runs in-process — this bound is what keeps a bad source from
    /// taking the media server down with it.
    /// </summary>
    /// <remarks>
    /// Raised from 3 once failed sessions stopped squatting their slots: a viewer who tries three
    /// dead sources in a row used to fill the cap, after which every further attempt was refused
    /// instantly — indistinguishable, from the client, from every remaining source also being dead.
    /// </remarks>
    public int MaxConcurrentStreamSessions { get; set; } = 8;

    /// <summary>Gets or sets how long a session with no reads survives before it is torn down.</summary>
    public int StreamSessionIdleMinutes { get; set; } = 20;

    /// <summary>
    /// Gets or sets how long opening a stream may take before it is abandoned, in seconds. Zero — the
    /// default — means no limit.
    /// </summary>
    /// <remarks>
    /// Session creation no longer blocks on the swarm at all: it returns a Preparing session
    /// immediately and reports live health while peers are found, so there is nothing for a deadline
    /// to protect. The old 60s bound routinely killed healthy, popular torrents that simply had not
    /// found their first peer yet, and reported them to the viewer as dead. Left configurable only
    /// for operators who genuinely want an upper bound; the code treats any value below 1 as none.
    /// </remarks>
    public int StreamOpenTimeoutSeconds { get; set; }

    /// <summary>Gets or sets the Prowlarr base URL (e.g., http://localhost:9696). Empty disables source discovery.</summary>
    public string ProwlarrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Prowlarr API key (sent as the X-Api-Key header).</summary>
    public string ProwlarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how long a source search is cached, in hours. Shared across users: the answer to
    /// "what can I stream for this title" barely changes minute to minute, and re-asking every
    /// indexer per viewer is the fastest way to get rate-limited.
    /// </summary>
    public int SourceSearchCacheHours { get; set; } = 6;

    // --- Peer discovery -------------------------------------------------------------------
    //
    // The four switches qBittorrent groups under "Privacy", mapped onto what MonoTorrent 3.0.2
    // actually exposes. Every default reproduces the behaviour that was hardcoded before these
    // existed, so an install that never opens the page behaves identically.
    //
    // qBittorrent's "anonymous mode" is deliberately absent: it suppresses the client fingerprint in
    // the peer id and the IP in tracker announces, and MonoTorrent offers no control over either.
    // A switch by that name would tell a viewer they are anonymous when they are not.

    /// <summary>
    /// Gets or sets a value indicating whether DHT is used to find peers.
    /// </summary>
    /// <remarks>
    /// Honest caveat: MonoTorrent 3.0.2's DHT has never bootstrapped on the measured hosts — it sends
    /// its queries and receives nothing back, logged as <c>dht=NotReady</c> for a session's whole
    /// life. The switch is real and wired; the peers it should produce are not yet. The Observatory
    /// tab reports the live DHT state rather than implying this setting delivers on its own.
    /// </remarks>
    public bool StreamEnableDht { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Peer Exchange may introduce peers found by other peers.</summary>
    public bool StreamEnablePeerExchange { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Local Peer Discovery multicasts on the LAN.
    /// </summary>
    /// <remarks>
    /// Costs nothing outbound and finds peers on the same network for free, but it does announce to
    /// the LAN what this server is downloading — the one switch here with a real privacy cost inside
    /// the household rather than outside it.
    /// </remarks>
    public bool StreamEnableLocalPeerDiscovery { get; set; } = true;

    /// <summary>
    /// Gets or sets peer-connection encryption: <c>allow</c>, <c>require</c> or <c>disable</c>.
    /// </summary>
    /// <remarks>
    /// The same three choices qBittorrent offers. <c>allow</c> prefers encrypted connections but
    /// accepts plaintext, which is the widest-reaching setting and the default. <c>require</c> refuses
    /// unencrypted peers, which is more private and strictly shrinks the usable swarm. Anything
    /// unrecognised is treated as <c>allow</c> rather than failing a stream over a typo.
    /// </remarks>
    public string StreamEncryptionMode { get; set; } = "allow";

    // --- Connectivity ---------------------------------------------------------------------

    /// <summary>
    /// Gets or sets the TCP port peers connect in on. Zero picks a random one each start.
    /// </summary>
    /// <remarks>
    /// Zero was the effective behaviour before this setting existed, and it quietly defeated the one
    /// lever that adds peers for free. Measured 2026-08-16: the engine listened on 46497, a different
    /// port every restart, so the UPnP mapping moved with it and a static router forward could not be
    /// configured at all. Behind NAT with no reachable port a client only ever talks to peers that
    /// accept inbound — the rest can see us and never reach us — which showed up as connections
    /// pinned at the half-open cap while the trackers reported dozens of idle seeds.
    ///
    /// Pinning a port is what makes inbound reachability provable. An inbound peer costs no outbound
    /// connection attempt, so it adds swarm capacity without touching the NAT budget below.
    /// </remarks>
    public int StreamListenPort { get; set; }

    /// <summary>Gets or sets a value indicating whether the router is asked to open that port over UPnP/NAT-PMP.</summary>
    public bool StreamAllowPortForwarding { get; set; } = true;

    /// <summary>
    /// Gets or sets the externally reachable address to advertise, when it differs from the one peers
    /// connect in on. Empty — the normal case — advertises whatever the listener sees.
    /// </summary>
    /// <remarks>
    /// Only needed when a port was forwarded manually to a different external port, or the server sits
    /// behind an address it cannot observe. Both this and <see cref="StreamReportedPort"/> must be set
    /// for either to take effect; half a pair would advertise an endpoint no peer can reach.
    /// </remarks>
    public string StreamReportedAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets the external port to advertise alongside <see cref="StreamReportedAddress"/>.</summary>
    public int StreamReportedPort { get; set; }

    // --- Peer budget ----------------------------------------------------------------------
    //
    // Editable, but the defaults do not move. See the comment on these constants' former home in
    // TorrentStreamService for why the half-open number in particular is not a speed knob.

    /// <summary>Gets or sets the ceiling on simultaneous peer connections across all sessions.</summary>
    public int StreamMaxConnections { get; set; } = 80;

    /// <summary>
    /// Gets or sets how many connection attempts may be in flight at once.
    /// </summary>
    /// <remarks>
    /// This is a budget against the household router's NAT table, not a download-speed setting. Raising
    /// it above 8 has twice coincided with the entire LAN losing connectivity — the August incident at
    /// 20 half-open with a 5s timeout, and again on 2026-08-13 at 20 with a 10s timeout, which reset an
    /// SSH session to the server mid-test. Prove inbound reachability with a forwarded port first; that
    /// adds peers without adding a single outbound attempt.
    /// </remarks>
    public int StreamMaxHalfOpenConnections { get; set; } = 8;

    /// <summary>Gets or sets how long an unanswered peer connection attempt is held before giving up.</summary>
    public int StreamConnectionTimeoutSeconds { get; set; } = 10;

    /// <summary>Gets or sets a download ceiling in KiB/s across all sessions. Zero is unlimited.</summary>
    public int StreamMaxDownloadRateKbps { get; set; }

    /// <summary>
    /// Gets or sets an upload ceiling in KiB/s across all sessions. Zero is unlimited.
    /// </summary>
    /// <remarks>
    /// Worth leaving unlimited: many peers reciprocate on upload rate, so throttling it tends to cost
    /// download speed on the very swarms that are already slow.
    /// </remarks>
    public int StreamMaxUploadRateKbps { get; set; }

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

    // --- LLM provider (any OpenAI-compatible endpoint; supersedes the Groq-only fields, which
    // remain as the legacy fallback so existing installs keep working) ----------------------

    /// <summary>
    /// Gets or sets the OpenAI-compatible base URL (e.g. https://api.openai.com/v1,
    /// http://localhost:11434/v1 for Ollama). Empty uses Groq (https://api.groq.com/openai/v1).
    /// </summary>
    public string LlmBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the LLM API key (Bearer auth). Empty falls back to <see cref="GroqApiKey"/>; keyless local endpoints need only a base URL.</summary>
    public string LlmApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the chat model id. Empty falls back to <see cref="GroqModel"/>.</summary>
    public string LlmModel { get; set; } = string.Empty;

    // --- LLM discovery (SuggestArr-style candidate generation; rows only, never auto-requests) ---

    /// <summary>
    /// Gets or sets a value indicating whether the LLM generates external discovery picks from the
    /// viewer's watch history (the primary source for You Might Like / Because You Watched when on;
    /// TMDB sources fill when it under-delivers). Needs an LLM endpoint/key AND a TMDB key (title
    /// resolution). Surfaces rows only — never requests content. Off by default.
    /// </summary>
    public bool FeatureLlmDiscovery { get; set; }

    /// <summary>Gets or sets how many recommendations each per-media-type LLM call asks for (1-25).</summary>
    public int LlmDiscoveryMaxPerMediaType { get; set; } = 10;

    /// <summary>Gets or sets how many watch-history titles are sent per LLM call (5-40).</summary>
    public int LlmDiscoveryHistoryCap { get; set; } = 20;

    /// <summary>
    /// Gets or sets a value indicating whether the LLM curates every personalized surface — For You +
    /// Spotlight (full selection from a wide local pool, not just re-ranking), mood collections,
    /// More Like This, and the Coming Soon taste filter. List rows (trending, country, recently
    /// added) stay algorithmic. Fail-soft per surface to the local engine. Off by default.
    /// </summary>
    public bool FeatureLlmCuration { get; set; }

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
