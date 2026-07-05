using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Wholphin.Engine.Analytics;
using Wholphin.Engine.Behavior;
using Wholphin.Engine.Caching;
using Wholphin.Engine.Catalog;
using Wholphin.Engine.Data;
using Wholphin.Engine.Diagnostics;
using Wholphin.Engine.Embedding;
using Wholphin.Engine.Home;
using Wholphin.Engine.Integrations.Jellyseerr;
using Wholphin.Engine.Llm;
using Wholphin.Engine.Personalization;
using Wholphin.Engine.Presentation;
using Wholphin.Engine.Recommendation;
using Wholphin.Engine.Requests;
using Wholphin.Engine.Settings;
using Wholphin.Engine.Stores;
using Wholphin.Engine.Sync;

namespace Wholphin.Engine;

/// <summary>
/// Registers Orca Engine services into Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Bound the shared cache so it can't grow without limit. Every InMemoryCache entry is size 1,
        // so this caps the entry count; entries are evicted under pressure (all have TTLs anyway).
        serviceCollection.AddMemoryCache(o => o.SizeLimit = 4096);

        // Operational metrics (in-process counters surfaced via /Admin/Metrics).
        serviceCollection.AddSingleton<IEngineMetrics, EngineMetrics>();

        // Data layer
        serviceCollection.AddSingleton<IWholphinDbContextFactory, WholphinDbContextFactory>();
        serviceCollection.AddSingleton<ICache, InMemoryCache>();
        serviceCollection.AddSingleton<IProfileStore, ProfileStore>();
        serviceCollection.AddSingleton<IMetadataIndex, MetadataIndex>();

        // Settings: canonical roaming store + layered resolver (system → admin → group → user)
        // that produces effective feature flags + tunables for the home generator and bootstrap.
        serviceCollection.AddSingleton<IUserSettingsStore, UserSettingsStore>();
        serviceCollection.AddSingleton<ISettingsService, SettingsService>();

        // Presentation (dynamic card decision)
        serviceCollection.AddSingleton<ICardSelector, DefaultCardSelector>();

        // Creates the SQLite database (schema + WAL) on startup (runs before sync).
        serviceCollection.AddHostedService<DatabaseInitializer>();

        // Sync engine: projects the Jellyfin library into the catalog.
        serviceCollection.AddSingleton<ILibrarySyncService, LibrarySyncService>();
        serviceCollection.AddHostedService<LibrarySyncEntryPoint>();

        // Behavior engine: captures playback + explicit-feedback signals into the append-only log.
        serviceCollection.AddSingleton<IBehaviorService, BehaviorService>();
        serviceCollection.AddHostedService<BehaviorEntryPoint>();

        // Personalization: behavior log → per-user affinity vectors (weighted, decayed, confidence-scored).
        serviceCollection.AddSingleton<IPersonalizationService, PersonalizationService>();

        // Deterministic explanation engine: every recommended card is self-explaining without the
        // LLM (which, when configured, overrides these with richer copy).
        serviceCollection.AddSingleton<Explanation.IExplanationService, Explanation.ExplanationService>();

        // Per-user taste embedding file ({DataPath}/wholphin-engine/profiles/{userId}.json):
        // seeds + taste vector derived from the content-vector snapshot; drives taste-matched pulls.
        serviceCollection.AddSingleton<ITasteProfileService, TasteProfileService>();

        // Shared scoring policy: one config-driven source of ranking weights for the recommender
        // and the discovery scorer (defaults reproduce the historical constants).
        serviceCollection.AddSingleton<Ranking.IScoringPolicy, Ranking.ScoringPolicy>();

        // Recommender v1: content-based hybrid ranking over the catalog.
        serviceCollection.AddSingleton<IRecommender, ContentRecommender>();

        // Pluggable embeddings: local TF-IDF (default) + optional hosted/local adapters, chosen by
        // admin config and resolved via IEmbeddingService (fails soft back to TF-IDF).
        serviceCollection.AddSingleton<IEmbeddingProvider, TfIdfEmbeddingProvider>();
        serviceCollection.AddSingleton<IEmbeddingProvider, OpenAiEmbeddingProvider>();
        serviceCollection.AddSingleton<IEmbeddingProvider, GeminiEmbeddingProvider>();
        serviceCollection.AddSingleton<IEmbeddingProvider, VoyageEmbeddingProvider>();
        serviceCollection.AddSingleton<IEmbeddingProvider, JinaEmbeddingProvider>();
        serviceCollection.AddSingleton<IEmbeddingProvider, OnnxEmbeddingProvider>();
        serviceCollection.AddSingleton<IEmbeddingService, EmbeddingService>();

        // Local-first AI: content-vector index over the catalog (thematic similarity; cached).
        serviceCollection.AddSingleton<IContentVectorIndex, ContentVectorIndex>();

        // Item similarity: content-based "More Like This" / "Because You Watched X" (on-demand + cached).
        // Blends flat set-overlap with the TF-IDF cosine above.
        serviceCollection.AddSingleton<ISimilarityService, SimilarityService>();

        // Genre relationship graph: offline co-occurrence associations (cached).
        serviceCollection.AddSingleton<IGenreGraphService, GenreGraphService>();

        // Milestone 8: mood-based collection rows (Mind Bending, Dark Thrillers, …; rotated daily).
        serviceCollection.AddSingleton<IMoodCollectionService, MoodCollectionService>();

        // Opt-in Stage-3 LLM re-ranker (Groq over OpenAI-compatible HTTP) — additive polish on the
        // "For You" row (reorder + generated title + "why"); self-gating + fail-soft, off until a key is set.
        serviceCollection.AddSingleton<ILlmProvider, GroqLlmProvider>();
        serviceCollection.AddSingleton<ILlmReRanker, LlmReRanker>();

        // Impression funnel analytics (shown→focused→clicked→played→completed; cached).
        serviceCollection.AddSingleton<IFunnelAnalytics, FunnelAnalytics>();
        // Milestone 8: local Wholphin community rating aggregated from behavior signals.
        serviceCollection.AddSingleton<ICommunityRatingService, CommunityRatingService>();
        // Milestone 8: "Did You Know?" trivia (Groq-first, TMDB-keyword fallback; cached).
        serviceCollection.AddSingleton<Metadata.ITriviaProvider, Metadata.TriviaProvider>();
        // Content advisories: per movie/series warnings (Groq), cached durably + pre-generated in the background.
        serviceCollection.AddSingleton<Metadata.IContentWarningProvider, Metadata.ContentWarningProvider>();
        serviceCollection.AddSingleton<Metadata.IContentWarningEnricher, Metadata.ContentWarningEnricher>();
        // Milestone 8: popularity leaderboards (Most Watched/Completed/Requested/Rewatched/Dropped).
        serviceCollection.AddSingleton<IPopularityService, PopularityService>();

        // Autonomous worker: one instance serving both the dirty-queue port and the hosted lifecycle.
        // Recomputes affinity + precomputes the home read-model when behavior events arrive.
        serviceCollection.AddSingleton<ProfileRecomputeWorker>();
        serviceCollection.AddSingleton<IProfileRecomputeQueue>(sp => sp.GetRequiredService<ProfileRecomputeWorker>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ProfileRecomputeWorker>());

        // Continue Watching: reads Jellyfin's per-user resume points (behind a port).
        serviceCollection.AddSingleton<IResumeProvider, JellyfinResumeProvider>();

        // Milestone 8 home providers: "New Since You Were Away" + "Coming Soon".
        serviceCollection.AddSingleton<INewSinceProvider, NewSinceProvider>();
        serviceCollection.AddSingleton<Catalog.IUpcomingProvider, Catalog.UpcomingProvider>();

        // Pluggable home-row providers: the justified discovery rows (trending + taste blend,
        // You Might Like, pulled Because You Watched, country) — future rows register here too.
        serviceCollection.AddSingleton<IRowProvider, DiscoveryRowProvider>();

        // Home generator (content-based in v1; personalization/recommender layer in later).
        serviceCollection.AddSingleton<HomeService>();

        // Milestone 2: unified availability-aware catalog (Jellyseerr/TMDB).
        // Fail-soft port over Jellyseerr (requests + availability + discovery).
        serviceCollection.AddSingleton<IJellyseerrClient, JellyseerrClient>();
        // Milestone 7: fail-soft port over TMDB (genre maps + metadata/artwork enrichment + direct discovery).
        serviceCollection.AddSingleton<Integrations.Tmdb.ITmdbClient, Integrations.Tmdb.TmdbClient>();
        // Studio/provider tag: caches TMDB watch-provider logos on disk + tags catalog rows with their brand.
        serviceCollection.AddSingleton<Integrations.Tmdb.IProviderLogoCache, Integrations.Tmdb.ProviderLogoCache>();
        serviceCollection.AddSingleton<IWatchProviderEnricher, WatchProviderEnricher>();
        // Milestone 8: fail-soft port over Sonarr/Radarr calendars (air dates for New Since Away + Coming Soon).
        serviceCollection.AddSingleton<Integrations.Arr.IArrClient, Integrations.Arr.ArrClient>();
        // Engine-proxied requests (captures request affinity + reflects availability).
        serviceCollection.AddSingleton<IRequestService, RequestService>();
        // Taste-driven discovery pipeline: pluggable candidate sources (same IEnumerable pattern as
        // the embedding providers) + the strict stage chain + the orchestrator port. Every external
        // title shown traces to a justification pick written here.
        serviceCollection.AddSingleton<Discovery.IDiscoverySource, Discovery.Sources.SeedRelatedSource>();
        serviceCollection.AddSingleton<Discovery.IDiscoverySource, Discovery.Sources.TasteDiscoverSource>();
        serviceCollection.AddSingleton<Discovery.IDiscoverySource, Discovery.Sources.TrendingSource>();
        serviceCollection.AddSingleton<Discovery.IDiscoverySource, Discovery.Sources.CountrySource>();
        serviceCollection.AddSingleton<Discovery.EligibilityFilter>();
        serviceCollection.AddSingleton<Discovery.CandidateScorer>();
        serviceCollection.AddSingleton<Discovery.DiversityStage>();
        serviceCollection.AddSingleton<Discovery.SelectionStage>();
        serviceCollection.AddSingleton<Discovery.PickPersistence>();
        serviceCollection.AddSingleton<Discovery.IDiscoveryOrchestrator, Discovery.DiscoveryPipeline>();
        // Backfills genres + artwork + trailer onto requestable rows from TMDB (gated on a TMDB key).
        serviceCollection.AddSingleton<ICatalogEnricher, TmdbEnricher>();
        // Advances in-flight (Requested/Downloading) items through the availability state machine.
        serviceCollection.AddSingleton<IAvailabilityReconciler, AvailabilityReconciler>();
        // Periodic maintenance: reconcile availability + TMDB enrichment + refresh discovery imports (gated, fail-soft).
        serviceCollection.AddHostedService<JellyseerrMaintenanceWorker>();

        // Server-side trailer pre-buffer/transcode (yt-dlp + ffmpeg; fail-soft, dormant until the
        // binaries are installed). Trailer redesign: an explicit state machine (TrailerStateStore) plus
        // a single bounded priority queue (TrailerQueueWorker serves both the ITrailerQueue port and the
        // hosted worker-pool lifecycle) so production is prioritised by user visibility and can never
        // swamp the host with concurrent yt-dlp/ffmpeg processes. Producers (controller, pre-buffer
        // worker, scheduled task) enqueue; the pool executes.
        serviceCollection.AddSingleton<Trailer.ITrailerStateStore, Trailer.TrailerStateStore>();
        serviceCollection.AddSingleton<Trailer.ITrailerService, Trailer.TrailerService>();
        serviceCollection.AddSingleton<Trailer.TrailerQueueWorker>();
        serviceCollection.AddSingleton<Trailer.ITrailerQueue>(sp => sp.GetRequiredService<Trailer.TrailerQueueWorker>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Trailer.TrailerQueueWorker>());
        // Predictive warming (Phase 7): ranks what to warm from CW + engagement + interest + popularity.
        serviceCollection.AddSingleton<Trailer.ITrailerPredictionService, Trailer.TrailerPredictionService>();
        serviceCollection.AddHostedService<Trailer.TrailerPrebufferWorker>();
        // Cache lifecycle (Phase 8): bounds size/count, evicts by LFU×recency (pinning frequent views), TTL + integrity.
        serviceCollection.AddHostedService<Trailer.TrailerCacheManager>();

        // LAN app-update channel: mirrors the newest Orca X GitHub release + disk-caches its APKs,
        // so every TV client updates from the server instead of re-downloading from GitHub.
        serviceCollection.AddSingleton<Wholphin.Engine.Update.IAppUpdateService, Wholphin.Engine.Update.AppUpdateService>();
    }
}
