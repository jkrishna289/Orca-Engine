# Graph Report - wholphine-Engine-Plugin  (2026-08-20)

## Corpus Check
- 356 files · ~233,537 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 4092 nodes · 9301 edges · 211 communities (199 shown, 12 thin omitted)
- Extraction: 95% EXTRACTED · 5% INFERRED · 0% AMBIGUOUS · INFERRED: 462 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- Requests and *arr Controllers
- User Settings API
- In-Memory Cache Layer
- MonoTorrent Session Management
- Plugin Bootstrap and Configuration
- Trailer Scoring Tests
- Home Render Contract
- Namespace Fabric (Core)
- External Process Invocation
- Catalog Metadata Index
- Recommendation Explanation
- Architecture and Findings Docs
- Torrent Stream Controller
- Outbound HTTP and Metrics
- Genre Graph API
- Namespace Fabric (Discovery)
- Namespace Fabric (Infrastructure)
- Trailer State Machine
- Namespace Fabric (Personalization)
- Torrent Swarm Scraping
- Recommendations API
- App Update Channel
- Card Contract Enums
- Affinity Vector Model
- Watch History Import
- Engine Alerts and Failure Paths
- Metadata Merge Tests
- Stream Health Diagnosis
- Behavior Signal Ingestion
- TMDB Catalog Enrichment
- Media Identity Resolution
- Trailer State Store
- Torrent Settings Tests
- Jellyfin Playback Events
- Community Rating Service
- Metadata Provider Port
- Observatory Streaming Page
- Per-Item Recommendation Memory
- TMDB HTTP Client
- TMDB Response Models
- Observatory API Client
- Observatory Core Pages
- Observatory Detail Pages
- Provider HTTP Gate
- Client Bootstrap API
- Jellyfin User Enumeration
- Ollama and OpenAI Embeddings
- LLM History Prompting
- Observatory Build Toolchain
- Watch History Event Synthesis
- Cluster 50
- Cluster 51
- Cluster 52
- Cluster 53
- Cluster 54
- Cluster 55
- Cluster 56
- Torrent Source Ranking
- Embedding Batching Tests
- Cluster 59
- Cluster 60
- Cluster 61
- Cluster 62
- Cluster 63
- Cluster 64
- Cluster 65
- Cluster 66
- Cluster 67
- Cluster 68
- Cluster 69
- Cluster 70
- Cluster 71
- Cluster 72
- Cluster 73
- Cluster 74
- Cluster 75
- Cluster 76
- Cluster 77
- Cluster 78
- Cluster 79
- Cluster 80
- Cluster 81
- Cluster 82
- Cluster 83
- Cluster 84
- Cluster 85
- Cluster 86
- Cluster 87
- Cluster 88
- Cluster 89
- Cluster 90
- Cluster 91
- Cluster 92
- Cluster 93
- Cluster 94
- Cluster 95
- Cluster 96
- Cluster 97
- Cluster 98
- Cluster 99
- Cluster 100
- Cluster 101
- Cluster 102
- Cluster 103
- Cluster 104
- Cluster 105
- Cluster 106
- Cluster 107
- Cluster 108
- Cluster 109
- Cluster 110
- Cluster 111
- Cluster 112
- Cluster 113
- Cluster 114
- Cluster 115
- Cluster 116
- Cluster 117
- Cluster 118
- Cluster 119
- Cluster 120
- Cluster 121
- Cluster 122
- Cluster 123
- Cluster 124
- Cluster 125
- Cluster 126
- Cluster 127
- Cluster 128
- Cluster 129
- Cluster 130
- Cluster 131
- Cluster 132
- Cluster 133
- Cluster 134
- Cluster 135
- Cluster 136
- Cluster 137
- Cluster 138
- Cluster 139
- Cluster 140
- Cluster 141
- Cluster 142
- Cluster 143
- Cluster 144
- Cluster 145
- Cluster 146
- Cluster 147
- Cluster 148
- Cluster 149
- Cluster 150
- Cluster 151
- Cluster 152
- Cluster 153
- Cluster 154
- Cluster 155
- Cluster 156
- Cluster 157
- Cluster 158
- Cluster 159
- Cluster 160
- Cluster 161
- Cluster 162
- Cluster 163
- Cluster 164
- Cluster 165
- Cluster 166
- Cluster 167
- Cluster 168
- Cluster 169
- Cluster 170
- Cluster 171
- Cluster 172
- Cluster 173
- Cluster 174
- Cluster 175
- Cluster 176
- Cluster 177
- Cluster 178
- Cluster 179
- Cluster 180
- Cluster 181
- Cluster 182
- Cluster 183
- Cluster 184
- Cluster 185
- Cluster 186
- Cluster 187
- Cluster 188
- Cluster 189
- Cluster 190
- Cluster 191
- Cluster 192
- Cluster 193
- Cluster 194
- Cluster 195
- Cluster 196
- Cluster 197
- Cluster 198
- Cluster 199
- Cluster 200
- Cluster 201
- Cluster 202
- Cluster 203
- Cluster 204
- Cluster 205
- Cluster 206
- Cluster 207
- Cluster 208
- Cluster 209

## God Nodes (most connected - your core abstractions)
1. `Wholphin.Engine.Data.Enums` - 96 edges
2. `CatalogItem` - 95 edges
3. `MediaType` - 86 edges
4. `Wholphin.Engine.Data.Entities` - 84 edges
5. `TorrentStreamService` - 63 edges
6. `Wholphin.Engine.Diagnostics` - 57 edges
7. `Wholphin.Engine.Tests` - 56 edges
8. `Wholphin.Engine.Data` - 54 edges
9. `IWholphinDbContextFactory` - 52 edges
10. `Wholphin.Engine.Personalization` - 49 edges

## Surprising Connections (you probably didn't know these)
- `Orca Engine` --illustrated_by--> `Engine banner artwork`  [AMBIGUOUS]
  README.md → docs/assets/engine-banner.svg
- `Orca Engine` --provides--> `Analytics and funnels`  [EXTRACTED]
  README.md → docs/assets/architecture.svg
- `Orca Engine` --provides--> `Catalog reconciler`  [EXTRACTED]
  README.md → docs/assets/architecture.svg
- `Orca Engine` --enriches_from--> `TMDB`  [EXTRACTED]
  README.md → docs/assets/architecture.svg
- `Orca Engine` --provides--> `Trailer queue`  [EXTRACTED]
  README.md → docs/assets/architecture.svg

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **** — cause_language_never_learned, cause_document_no_language, cause_silent_fallback, cause_popularity_pool, taste_language_gap [EXTRACTED]
- **** — state_request, state_requested, state_downloading, state_recently_added, state_watch_now [EXTRACTED]
- **** — behaviour_signals, taste_profiles, ranking_calibration, home_composition, discovery_pipeline [EXTRACTED]

## Communities (211 total, 12 thin omitted)

### Community 0 - "Requests and *arr Controllers"
Cohesion: 0.05
Nodes (34): ConcurrentQueue, ActionResult, AllowAnonymous, CancellationToken, HttpGet, IActionResult, IArrClient, IJellyseerrClient (+26 more)

### Community 1 - "User Settings API"
Cohesion: 0.07
Nodes (38): Reason, UserSettingRequest, ActionResult, AllowAnonymous, CancellationToken, Guid, HttpDelete, HttpGet (+30 more)

### Community 2 - "In-Memory Cache Layer"
Cohesion: 0.09
Nodes (26): IDisposable, MemoryCache, int, TimeSpan, InMemoryCache, IReadOnlyList, EmbeddingProgress, EmbeddingRun (+18 more)

### Community 3 - "MonoTorrent Session Management"
Cohesion: 0.07
Nodes (31): ITorrentManagerFile, MagnetLink, ResolvedTorrent, Timer, Torrent, TorrentManager, bool, CancellationToken (+23 more)

### Community 4 - "Plugin Bootstrap and Configuration"
Cohesion: 0.08
Nodes (30): BasePlugin, BasePluginConfiguration, Gate, IHasWebPages, Metrics, PluginPageInfo, State, PluginConfiguration (+22 more)

### Community 5 - "Trailer Scoring Tests"
Cohesion: 0.08
Nodes (21): Fact, InlineData, int, Theory, TrailerSearchScoreTests, CancellationToken, Func, ILogger (+13 more)

### Community 6 - "Home Render Contract"
Cohesion: 0.10
Nodes (29): LibraryRows, PendingRow, PersonalResult, List, ClientCapabilities, RenderBundle, DateTime, BootstrapResponse (+21 more)

### Community 7 - "Namespace Fabric (Core)"
Cohesion: 0.07
Nodes (8): Wholphin.Engine.Intelligence, Wholphin.Engine.Contracts, Wholphin.Engine.Sync, Wholphin.Engine.Data.Entities, Wholphin.Engine.Presentation, Wholphin.Engine.Home, Wholphin.Engine.Integrations.Arr, Wholphin.Engine.Stores

### Community 8 - "External Process Invocation"
Cohesion: 0.09
Nodes (21): FileStream, CancellationToken, ILogger, Process, Task, ExternalProcess, CancellationToken, Func (+13 more)

### Community 9 - "Catalog Metadata Index"
Cohesion: 0.08
Nodes (24): PersonInfo, CancellationToken, Guid, Task, IMetadataIndex, BaseItem, BaseItemKind, Dictionary (+16 more)

### Community 10 - "Recommendation Explanation"
Cohesion: 0.11
Nodes (26): DateTime, Guid, CatalogItem, ExplanationService, IExplanationService, CancellationToken, Guid, IReadOnlyDictionary (+18 more)

### Community 11 - "Architecture and Findings Docs"
Cohesion: 0.07
Nodes (44): Admin and settings, Analytics and funnels, Engine banner artwork, Behaviour signals, Catalog reconciler, Embedding document had no language, Language never learned from the library, Candidate pool was a global popularity contest (+36 more)

### Community 12 - "Torrent Stream Controller"
Cohesion: 0.09
Nodes (21): ActionResult, AllowAnonymous, Authorize, CancellationToken, HttpDelete, HttpGet, HttpPost, IActionResult (+13 more)

### Community 13 - "Outbound HTTP and Metrics"
Cohesion: 0.15
Nodes (16): CancellationToken, ConcurrentDictionary, HttpClient, HttpMethod, HttpRequestMessage, HttpResponseMessage, IHttpClientFactory, ILogger (+8 more)

### Community 14 - "Genre Graph API"
Cohesion: 0.07
Nodes (31): ActionResult, AllowAnonymous, CancellationToken, HttpGet, IReadOnlyDictionary, IReadOnlyList, List, Task (+23 more)

### Community 15 - "Namespace Fabric (Discovery)"
Cohesion: 0.09
Nodes (6): Wholphin.Engine.Discovery, Wholphin.Engine.Requests, Wholphin.Engine.Data.Enums, Wholphin.Engine.Integrations.Jellyseerr, Wholphin.Engine.Trailer, Wholphin.Engine.Settings

### Community 16 - "Namespace Fabric (Infrastructure)"
Cohesion: 0.10
Nodes (6): Wholphin.Engine.Metadata.Providers, Wholphin.Engine.Caching, Wholphin.Engine.Llm, Wholphin.Engine.Configuration, Wholphin.Engine.Http, Wholphin.Engine.Diagnostics

### Community 17 - "Trailer State Machine"
Cohesion: 0.09
Nodes (24): ITrailerSource, TrailerState, IWholphinDbContextFactory, CancellationToken, Guid, IReadOnlyDictionary, Task, UserSettingsStore (+16 more)

### Community 18 - "Namespace Fabric (Personalization)"
Cohesion: 0.08
Nodes (5): Wholphin.Engine.Explanation, Wholphin.Engine.Embedding, Wholphin.Engine.Recommendation, Wholphin.Engine.Personalization, Wholphin.Engine.Controllers

### Community 19 - "Torrent Swarm Scraping"
Cohesion: 0.10
Nodes (20): Leechers, Seeders, UdpClient, CancellationToken, Dictionary, IEngineMetrics, IHttpClientFactory, ILogger (+12 more)

### Community 20 - "Recommendations API"
Cohesion: 0.08
Nodes (27): RecommendationDto, ActionResult, AllowAnonymous, CancellationToken, Guid, HttpGet, IReadOnlyList, Task (+19 more)

### Community 21 - "App Update Channel"
Cohesion: 0.09
Nodes (25): Wholphin.Engine.Update, ActionResult, AllowAnonymous, CancellationToken, HttpGet, IActionResult, List, Task (+17 more)

### Community 22 - "Card Contract Enums"
Cohesion: 0.11
Nodes (19): CardAction, CardAspectRatio, CardImageType, CardSize, CardType, MediaSource, Guid, CardBadge (+11 more)

### Community 23 - "Affinity Vector Model"
Cohesion: 0.15
Nodes (17): DateTime, Dictionary, AffinityVector, CancellationToken, DateTime, Dictionary, double, Guid (+9 more)

### Community 24 - "Watch History Import"
Cohesion: 0.10
Nodes (24): Events, SeriesTally, Unresolved, User, BaseItem, BaseItemKind, bool, CancellationToken (+16 more)

### Community 25 - "Engine Alerts and Failure Paths"
Cohesion: 0.11
Nodes (17): CancellationToken, Func, ILogger, int, IProgress, IReadOnlyDictionary, IReadOnlyList, string (+9 more)

### Community 27 - "Stream Health Diagnosis"
Cohesion: 0.14
Nodes (6): double, StreamDiagnosis, StreamDiagnostics, InboundReachability, Fact, StreamDiagnosticsTests

### Community 28 - "Behavior Signal Ingestion"
Cohesion: 0.12
Nodes (23): BehaviorEventDto, FeedbackRequest, TelemetryBatch, TelemetryEvent, ActionResult, AllowAnonymous, Authorize, CancellationToken (+15 more)

### Community 29 - "TMDB Catalog Enrichment"
Cohesion: 0.15
Nodes (17): CancellationToken, ILogger, Task, TmdbEnricher, CancellationToken, ILogger, Task, WatchProviderEnricher (+9 more)

### Community 30 - "Media Identity Resolution"
Cohesion: 0.08
Nodes (22): CancellationToken, Task, IMediaIdentityResolver, CancellationToken, IReadOnlyCollection, Task, IMetadataAggregator, Guid (+14 more)

### Community 31 - "Trailer State Store"
Cohesion: 0.14
Nodes (13): CancellationToken, Task, ITrailerStateStore, bool, CancellationToken, IApplicationPaths, ILogger, int (+5 more)

### Community 32 - "Torrent Settings Tests"
Cohesion: 0.19
Nodes (6): EncryptionType, Fact, InlineData, string, Theory, StreamSettingsTests

### Community 33 - "Jellyfin Playback Events"
Cohesion: 0.12
Nodes (16): ISessionManager, PlaybackProgressEventArgs, PlaybackStopEventArgs, UserDataSaveEventArgs, BaseItem, CancellationToken, CancellationTokenSource, ILogger (+8 more)

### Community 34 - "Community Rating Service"
Cohesion: 0.09
Nodes (17): CancellationToken, ILogger, Task, CommunityRatingService, IEngineMetrics, CancellationToken, ILogger, Task (+9 more)

### Community 35 - "Metadata Provider Port"
Cohesion: 0.12
Nodes (15): CancellationToken, Task, IMetadataProvider, IEnumerable, MetadataCapability, Dictionary, int, IReadOnlyCollection (+7 more)

### Community 36 - "Observatory Streaming Page"
Cohesion: 0.14
Nodes (26): pick(), toAlert(), toSnapshot(), bool(), Check, Connectivity, dhtHint(), forwardHint() (+18 more)

### Community 37 - "Per-Item Recommendation Memory"
Cohesion: 0.19
Nodes (11): DateTime, Guid, UserItemMemory, DateTime, double, int, TimeSpan, InterestModel (+3 more)

### Community 38 - "TMDB HTTP Client"
Cohesion: 0.23
Nodes (13): CancellationToken, DateTime, HttpRequestMessage, IHttpClientFactory, ILogger, IReadOnlyList, JsonSerializerOptions, string (+5 more)

### Community 39 - "TMDB Response Models"
Cohesion: 0.14
Nodes (26): DateTime, Dictionary, IReadOnlyList, List, TmdbCollection, TmdbCredits, TmdbDetail, TmdbDiscoverCategory (+18 more)

### Community 40 - "Observatory API Client"
Cohesion: 0.13
Nodes (23): Fetched, get(), post(), postFor(), postJson(), Sample, toEvent(), useEventStream() (+15 more)

### Community 41 - "Observatory Core Pages"
Cohesion: 0.16
Nodes (24): EngineAlert, rate(), series(), Cache(), cpuPercent(), EngineHealth(), Overview(), Performance() (+16 more)

### Community 42 - "Observatory Detail Pages"
Cohesion: 0.11
Nodes (26): EngineEvent, useHistory(), usePoll(), Dashboard(), Embeddings(), GraphUser, HistoryImport(), ImportProgress (+18 more)

### Community 43 - "Provider HTTP Gate"
Cohesion: 0.12
Nodes (14): Body, Status, CancellationToken, HttpClient, HttpStatusCode, JsonSerializerOptions, Task, ProviderHttp (+6 more)

### Community 44 - "Client Bootstrap API"
Cohesion: 0.12
Nodes (19): BootstrapResponse, ActionResult, AllowAnonymous, CancellationToken, Guid, HttpGet, Task, HomeController (+11 more)

### Community 45 - "Jellyfin User Enumeration"
Cohesion: 0.13
Nodes (14): FakeUser, Guid, IReadOnlyList, Type, JellyfinUsers, Fact, Guid, IEnumerable (+6 more)

### Community 46 - "Ollama and OpenAI Embeddings"
Cohesion: 0.11
Nodes (19): EmbeddingDatum, string, OllamaEmbeddingProvider, CancellationToken, Dictionary, IHttpClientFactory, ILogger, IReadOnlyList (+11 more)

### Community 47 - "LLM History Prompting"
Cohesion: 0.10
Nodes (21): Loved, NormalizedTitles, Watched, DateTime, Guid, HistoryLine, IReadOnlyDictionary, IReadOnlyList (+13 more)

### Community 48 - "Observatory Build Toolchain"
Cohesion: 0.08
Nodes (24): react, react-dom, @types/react, @types/react-dom, typescript, vite, @vitejs/plugin-react, dependencies (+16 more)

### Community 49 - "Watch History Event Synthesis"
Cohesion: 0.18
Nodes (10): UserItemData, Guid, List, DateTime, Guid, BehaviorEvent, Fact, Guid (+2 more)

### Community 50 - "Cluster 50"
Cohesion: 0.14
Nodes (19): ContentWarningsDto, TriviaDto, ActionResult, AllowAnonymous, Authorize, CancellationToken, Guid, HttpGet (+11 more)

### Community 51 - "Cluster 51"
Cohesion: 0.12
Nodes (10): Wholphin.Engine.Ranking, Calibration, IScoringPolicy, RecommendationSignals, ScoringPolicy, ScoringWeights, Fact, InlineData (+2 more)

### Community 52 - "Cluster 52"
Cohesion: 0.16
Nodes (7): FanartResponse, OmdbResponse, ImageCandidate, MetadataFragment, MetadataFragment, Fact, MetadataProviderTests

### Community 53 - "Cluster 53"
Cohesion: 0.17
Nodes (18): PickRow, CancellationToken, DateTime, Dictionary, double, Guid, IEnumerable, ILogger (+10 more)

### Community 54 - "Cluster 54"
Cohesion: 0.17
Nodes (14): PrefetchItem, ActionResult, AllowAnonymous, Authorize, CancellationToken, HttpGet, HttpPost, IActionResult (+6 more)

### Community 55 - "Cluster 55"
Cohesion: 0.17
Nodes (14): CancellationToken, DateTime, Dictionary, Guid, HttpMethod, HttpRequestMessage, IHttpClientFactory, ILogger (+6 more)

### Community 56 - "Cluster 56"
Cohesion: 0.11
Nodes (18): CancellationToken, IReadOnlyList, Task, IMoodCollectionService, MoodRow, Func, HashSet, IReadOnlyList (+10 more)

### Community 57 - "Torrent Source Ranking"
Cohesion: 0.14
Nodes (10): double, IEnumerable, int, List, Regex, SourceRanker, IReadOnlyList, ReleaseTier (+2 more)

### Community 58 - "Embedding Batching Tests"
Cohesion: 0.32
Nodes (3): Fact, Task, EmbeddingBatchingTests

### Community 59 - "Cluster 59"
Cohesion: 0.13
Nodes (8): Wholphin.Engine.Analytics, Wholphin.Engine, Wholphin.Engine.Data, Wholphin.Engine.Catalog, IPluginServiceRegistrator, IServerApplicationHost, IServiceCollection, PluginServiceRegistrator

### Community 60 - "Cluster 60"
Cohesion: 0.13
Nodes (14): Wholphin.Engine.Integrations.Prowlarr, ProwlarrRelease, CancellationToken, HashSet, IHttpClientFactory, ILogger, IReadOnlyList, JsonSerializerOptions (+6 more)

### Community 61 - "Cluster 61"
Cohesion: 0.09
Nodes (16): DbContext, DbSet, ModelBuilder, DateTime, CatalogItemVector, DateTime, Guid, DiscoveryRun (+8 more)

### Community 62 - "Cluster 62"
Cohesion: 0.10
Nodes (15): float, IProgress, IReadOnlyList, ContentVector, Action, CancellationToken, CancellationTokenSource, HashSet (+7 more)

### Community 63 - "Cluster 63"
Cohesion: 0.12
Nodes (21): GeminiContent, GeminiEmbeddingValues, GeminiEmbedRequest, GeminiPart, CancellationToken, IHttpClientFactory, ILogger, int (+13 more)

### Community 64 - "Cluster 64"
Cohesion: 0.09
Nodes (22): DOM, DOM.Iterable, ES2020, src, vite.config.ts, compilerOptions, allowImportingTsExtensions, isolatedModules (+14 more)

### Community 65 - "Cluster 65"
Cohesion: 0.10
Nodes (20): TvdbRecord, TvdbToken, CancellationToken, Func, HttpClient, IHttpClientFactory, int, MediaIdentity (+12 more)

### Community 66 - "Cluster 66"
Cohesion: 0.15
Nodes (15): CancellationToken, ILogger, Task, AvailabilityReconciler, CancellationToken, Guid, IReadOnlyList, Task (+7 more)

### Community 67 - "Cluster 67"
Cohesion: 0.10
Nodes (14): IReadOnlyCollection, IReadOnlyList, IReadOnlyCollection, IReadOnlyList, DateTime, ProviderHealth, IReadOnlyList, CancellationToken (+6 more)

### Community 68 - "Cluster 68"
Cohesion: 0.17
Nodes (8): IReadOnlyList, JsonElement, LlmJsonParser, Fact, InlineData, string, Theory, LlmJsonParserTests

### Community 69 - "Cluster 69"
Cohesion: 0.21
Nodes (9): double, HashSet, IReadOnlyList, Regex, TorrentFileEntry, VideoFileSelector, Fact, long (+1 more)

### Community 70 - "Cluster 70"
Cohesion: 0.13
Nodes (3): Wholphin.Engine.Behavior, Wholphin.Engine.Tests, Wholphin.Engine.Streaming

### Community 71 - "Cluster 71"
Cohesion: 0.10
Nodes (16): IScheduledTask, CancellationToken, IEnumerable, IProgress, Task, TaskTriggerInfo, TimeSpan, ImportWatchHistoryTask (+8 more)

### Community 72 - "Cluster 72"
Cohesion: 0.16
Nodes (14): Result, CancellationToken, HashSet, ILogger, int, JsonElement, Ok, string (+6 more)

### Community 73 - "Cluster 73"
Cohesion: 0.22
Nodes (11): CancellationToken, Guid, HistoryLine, ILogger, int, IReadOnlyList, List, Task (+3 more)

### Community 74 - "Cluster 74"
Cohesion: 0.16
Nodes (8): CancellationToken, MediaIdentity, Task, CancellationToken, MediaIdentity, MetadataFragment, Task, Task

### Community 75 - "Cluster 75"
Cohesion: 0.25
Nodes (3): Fact, long, SourceRankerTests

### Community 76 - "Cluster 76"
Cohesion: 0.13
Nodes (13): DateTimeOffset, IHttpClientFactory, ILogger, int, SemaphoreSlim, string, TimeSpan, TrackerList (+5 more)

### Community 77 - "Cluster 77"
Cohesion: 0.15
Nodes (14): AdminStatus, ActionResult, CancellationToken, Dictionary, HttpGet, HttpPost, IActionResult, IArrClient (+6 more)

### Community 78 - "Cluster 78"
Cohesion: 0.25
Nodes (7): Aggregator, Cache, Fact, InlineData, Task, Theory, MetadataAggregatorTests

### Community 79 - "Cluster 79"
Cohesion: 0.15
Nodes (12): DiversityResult, Dictionary, double, int, IReadOnlyList, IScoringPolicy, List, DiversityResult (+4 more)

### Community 80 - "Cluster 80"
Cohesion: 0.13
Nodes (15): PendingJob, TrailerKey, CancellationToken, CancellationTokenSource, Dictionary, HashSet, ILogger, int (+7 more)

### Community 81 - "Cluster 81"
Cohesion: 0.12
Nodes (16): PersistenceResult, DateTime, Dictionary, Guid, DiscoveryRunReport, CancellationToken, ILogger, int (+8 more)

### Community 82 - "Cluster 82"
Cohesion: 0.17
Nodes (14): CommunityRatingDto, CancellationToken, Task, ICommunityRatingService, ActionResult, AllowAnonymous, Authorize, CancellationToken (+6 more)

### Community 83 - "Cluster 83"
Cohesion: 0.12
Nodes (16): Episode, CancellationToken, Guid, IReadOnlyList, Task, IResumeProvider, ResumePoint, BaseItemKind (+8 more)

### Community 84 - "Cluster 84"
Cohesion: 0.11
Nodes (13): HttpMessageHandler, IHttpClientFactory, CancellationToken, Func, HttpClient, HttpRequestMessage, HttpResponseMessage, HttpStatusCode (+5 more)

### Community 85 - "Cluster 85"
Cohesion: 0.15
Nodes (11): Dictionary, Exception, Fact, IReadOnlyDictionary, Key, List, Ok, Task (+3 more)

### Community 86 - "Cluster 86"
Cohesion: 0.17
Nodes (12): Hash, ItemId, CancellationToken, Dictionary, IEnumerable, int, IReadOnlyCollection, IReadOnlyList (+4 more)

### Community 87 - "Cluster 87"
Cohesion: 0.13
Nodes (12): OmdbRating, Func, IHttpClientFactory, List, MetadataCapability, string, OmdbMetadataProvider, OmdbRating (+4 more)

### Community 88 - "Cluster 88"
Cohesion: 0.17
Nodes (12): CancellationToken, Task, IAvailabilityReconciler, CancellationToken, CancellationTokenSource, IEngineMetrics, ILogger, int (+4 more)

### Community 89 - "Cluster 89"
Cohesion: 0.18
Nodes (13): DateTime, UpcomingItem, CancellationToken, DateTime, Guid, ILogger, int, IReadOnlyList (+5 more)

### Community 90 - "Cluster 90"
Cohesion: 0.20
Nodes (8): Affinity, Feature, double, IReadOnlyDictionary, IReadOnlyList, ExplanationTemplates, Fact, ExplanationTemplatesTests

### Community 91 - "Cluster 91"
Cohesion: 0.25
Nodes (12): CatalogStats, ActionResult, AllowAnonymous, Authorize, CancellationToken, Dictionary, HttpGet, HttpPost (+4 more)

### Community 92 - "Cluster 92"
Cohesion: 0.14
Nodes (13): IMetadataProvider, CancellationToken, Func, Task, IProviderGate, CancellationToken, ImageCandidate, int (+5 more)

### Community 93 - "Cluster 93"
Cohesion: 0.21
Nodes (11): DateTime, TrailerAsset, CancellationToken, CancellationTokenSource, DateTime, ILogger, int, long (+3 more)

### Community 94 - "Cluster 94"
Cohesion: 0.32
Nodes (4): Vector, Weight, Fact, ContentVectorTests

### Community 95 - "Cluster 95"
Cohesion: 0.16
Nodes (15): Dictionary, HashSet, List, ArrMonitoredSeries, ArrMonitorState, RadarrCalendarItem, RadarrHistoryItem, RadarrHistoryMovie (+7 more)

### Community 96 - "Cluster 96"
Cohesion: 0.18
Nodes (8): Action, IReadOnlyList, CatalogFeatures, ScalarDimension, Fact, InlineData, Theory, CatalogFeaturesTests

### Community 97 - "Cluster 97"
Cohesion: 0.17
Nodes (10): CancellationToken, EngineSettings, Fact, Func, int, long, Stream, Task (+2 more)

### Community 98 - "Cluster 98"
Cohesion: 0.19
Nodes (10): ItemChangeEventArgs, CancellationToken, CancellationTokenSource, Channel, Guid, ILibraryManager, ILogger, Task (+2 more)

### Community 99 - "Cluster 99"
Cohesion: 0.15
Nodes (13): CancellationToken, IReadOnlyList, Task, IPopularityService, PopularItem, PopularityReport, CancellationToken, Dictionary (+5 more)

### Community 100 - "Cluster 100"
Cohesion: 0.23
Nodes (11): CancellationToken, Dictionary, double, Guid, IApplicationPaths, ILogger, int, JsonSerializerOptions (+3 more)

### Community 101 - "Cluster 101"
Cohesion: 0.19
Nodes (12): CancellationToken, ILogger, int, IProgress, Task, TimeSpan, ContentVectorIndex, DateTime (+4 more)

### Community 102 - "Cluster 102"
Cohesion: 0.12
Nodes (13): CancellationToken, IReadOnlyList, Task, TmdbId, ITrailerPredictionService, CancellationToken, IEnumerable, int (+5 more)

### Community 105 - "Cluster 105"
Cohesion: 0.16
Nodes (12): ScoringResult, CancellationToken, DateTime, DiscoverResult, double, ILogger, IReadOnlyList, IScoringPolicy (+4 more)

### Community 106 - "Cluster 106"
Cohesion: 0.33
Nodes (10): ActionResult, AllowAnonymous, Authorize, CancellationToken, Guid, HttpGet, HttpPost, IActionResult (+2 more)

### Community 107 - "Cluster 107"
Cohesion: 0.18
Nodes (11): DateTime, Guid, UserProfile, CancellationToken, Guid, Task, IProfileStore, CancellationToken (+3 more)

### Community 108 - "Cluster 108"
Cohesion: 0.24
Nodes (5): Fact, InlineData, List, Theory, LlmCandidateSourceTests

### Community 109 - "Cluster 109"
Cohesion: 0.21
Nodes (10): CancellationToken, IReadOnlyList, Task, CancellationToken, DateTime, Guid, IReadOnlyList, IReadOnlySet (+2 more)

### Community 110 - "Cluster 110"
Cohesion: 0.34
Nodes (5): IReadOnlyList, SeasonProgress, DateTime, Fact, SeriesStateCalculatorTests

### Community 111 - "Cluster 111"
Cohesion: 0.24
Nodes (8): IReadOnlyList, string, StringBuilder, HistoryLine, LlmDiscoveryPromptBuilder, Fact, IReadOnlyList, LlmDiscoveryPromptBuilderTests

### Community 112 - "Cluster 112"
Cohesion: 0.18
Nodes (7): CancellationToken, IReadOnlyList, Task, Fact, InlineData, Theory, OpenAiCompatibleProviderTests

### Community 113 - "Cluster 113"
Cohesion: 0.23
Nodes (9): IReadOnlyDictionary, IReadOnlyList, ImageCandidate, MetadataFragment, Func, int, IReadOnlyDictionary, IReadOnlyList (+1 more)

### Community 114 - "Cluster 114"
Cohesion: 0.20
Nodes (12): Config, SettingsPanel(), save(), Field, FieldKind, Group, GROUPS, groupsFor() (+4 more)

### Community 115 - "Cluster 115"
Cohesion: 0.19
Nodes (12): AvailabilityStatusResponse, CreateRequest, ActionResult, AllowAnonymous, CancellationToken, Guid, HttpGet, HttpPost (+4 more)

### Community 116 - "Cluster 116"
Cohesion: 0.15
Nodes (15): ChatChoice, ChatMessage, ChatResponseFormat, IHttpClientFactory, ILogger, JsonSerializerOptions, List, string (+7 more)

### Community 117 - "Cluster 117"
Cohesion: 0.14
Nodes (11): ControllerBase, AllowAnonymous, HttpGet, IActionResult, string, ImagesController, CancellationToken, ContentType (+3 more)

### Community 118 - "Cluster 118"
Cohesion: 0.18
Nodes (10): Counts, FunnelStage, CancellationToken, Guid, int, Task, TimeSpan, Counts (+2 more)

### Community 119 - "Cluster 119"
Cohesion: 0.20
Nodes (7): HasResume, ActionResult, AllowAnonymous, HttpGet, IReadOnlyList, Item, CardsController

### Community 120 - "Cluster 120"
Cohesion: 0.14
Nodes (13): Microsoft.NET.Test.Sdk (17.12.0), MonoTorrent (3.0.2), xunit (2.9.2), xunit.runner.visualstudio (2.8.2), Wholphin.Engine.Tests, net9.0, Jellyfin.Controller (10.11.0), Microsoft.EntityFrameworkCore.Sqlite (9.0.10) (+5 more)

### Community 121 - "Cluster 121"
Cohesion: 0.19
Nodes (9): CancellationToken, Channel, Guid, ILogger, IReadOnlyList, Task, BehaviorService, Guid (+1 more)

### Community 122 - "Cluster 122"
Cohesion: 0.19
Nodes (9): TimeSpan, ICache, CancellationToken, ConcurrentDictionary, Func, Task, TimeSpan, Entry (+1 more)

### Community 123 - "Cluster 123"
Cohesion: 0.21
Nodes (10): CancellationToken, Guid, Task, ITasteProfileService, DateTime, Guid, IReadOnlyList, List (+2 more)

### Community 124 - "Cluster 124"
Cohesion: 0.16
Nodes (13): IDiscoverySource, DiscoveryPickKind, DiscoverySourceScope, double, ILogger, LlmCandidateSource, DiscoverySourceScope, double (+5 more)

### Community 125 - "Cluster 125"
Cohesion: 0.20
Nodes (10): SimilarResponse, MediaIdMapper, ActionResult, AllowAnonymous, CancellationToken, Guid, HttpGet, Task (+2 more)

### Community 126 - "Cluster 126"
Cohesion: 0.16
Nodes (11): SwarmScraper, ActionResult, Authorize, CancellationToken, HttpGet, ILogger, PluginConfiguration, Task (+3 more)

### Community 127 - "Cluster 127"
Cohesion: 0.26
Nodes (6): double, SignalWeights, Fact, InlineData, Theory, SignalWeightsTests

### Community 128 - "Cluster 128"
Cohesion: 0.22
Nodes (9): BehaviorEventType, CancellationToken, double, ILogger, int, IReadOnlyList, Task, TmdbId (+1 more)

### Community 129 - "Cluster 129"
Cohesion: 0.20
Nodes (9): CancellationToken, DateTime, Guid, ILibraryManager, IReadOnlyList, IReadOnlySet, IUserDataManager, IUserManager (+1 more)

### Community 130 - "Cluster 130"
Cohesion: 0.25
Nodes (9): CancellationToken, ILogger, int, IReadOnlyList, List, string, Task, TimeSpan (+1 more)

### Community 131 - "Cluster 131"
Cohesion: 0.16
Nodes (9): App(), Page, PAGES, host, style, Settings(), signOut(), AlertBanner() (+1 more)

### Community 132 - "Cluster 132"
Cohesion: 0.25
Nodes (9): CancellationToken, CancellationTokenSource, Channel, Guid, IEngineMetrics, ILogger, int, Task (+1 more)

### Community 133 - "Cluster 133"
Cohesion: 0.21
Nodes (10): CancellationToken, Task, CancellationToken, double, Guid, IEnumerable, IReadOnlyList, Task (+2 more)

### Community 134 - "Cluster 134"
Cohesion: 0.26
Nodes (5): double, HashSet, SimilarityScorer, Fact, SimilarityScorerTests

### Community 135 - "Cluster 135"
Cohesion: 0.24
Nodes (6): CachedFile, IEnumerable, List, Fact, long, StreamCacheTrimTests

### Community 136 - "Cluster 136"
Cohesion: 0.22
Nodes (9): EligibilityResult, List, DiscoveryCandidate, SourceAttribution, IReadOnlyDictionary, IReadOnlyList, EligibilityFilter, EligibilityResult (+1 more)

### Community 138 - "Cluster 138"
Cohesion: 0.15
Nodes (10): CancellationToken, DiscoveryCandidate, DiscoveryContext, IReadOnlyList, Task, CancellationToken, IReadOnlyList, Task (+2 more)

### Community 139 - "Cluster 139"
Cohesion: 0.18
Nodes (11): DateTime, Guid, ILogger, int, ContinueTheStoryRowProvider, Continuity, DateTime, TimeSpan (+3 more)

### Community 140 - "Cluster 140"
Cohesion: 0.18
Nodes (10): CancellationToken, ConcurrentDictionary, ContentType, IApplicationPaths, IHttpClientFactory, ILogger, Path, string (+2 more)

### Community 142 - "Cluster 142"
Cohesion: 0.36
Nodes (4): JsonElement, Fact, List, LlmCurationParseTests

### Community 143 - "Cluster 143"
Cohesion: 0.38
Nodes (4): ExplorationScorer, Fact, ExplorationScorerTests, TestData

### Community 144 - "Cluster 144"
Cohesion: 0.38
Nodes (5): Fact, JsonElement, SqliteConnection, Task, EmbeddingProbeTests

### Community 145 - "Cluster 145"
Cohesion: 0.27
Nodes (7): CatalogSummary, DateTime, IReadOnlyDictionary, AdminStatus, Fact, Task, CatalogSummaryTests

### Community 146 - "Cluster 146"
Cohesion: 0.29
Nodes (7): DbConnection, CancellationToken, ILogger, int, IReadOnlyList, Task, DatabaseInitializer

### Community 147 - "Cluster 147"
Cohesion: 0.29
Nodes (5): double, int, NegativeSignals, Fact, NegativeSignalsTests

### Community 148 - "Cluster 148"
Cohesion: 0.42
Nodes (8): ActionResult, AllowAnonymous, CancellationToken, Guid, HttpGet, HttpPost, Task, PersonalizationController

### Community 149 - "Cluster 149"
Cohesion: 0.27
Nodes (8): CancellationToken, Guid, IReadOnlyDictionary, Task, GlobalPullResult, IDiscoveryOrchestrator, SweepResult, TastePullResult

### Community 150 - "Cluster 150"
Cohesion: 0.20
Nodes (10): CancellationToken, DiscoverResult, DiscoveryCandidate, DiscoveryContext, DiscoverySourceScope, double, int, IReadOnlyList (+2 more)

### Community 151 - "Cluster 151"
Cohesion: 0.23
Nodes (10): CancellationToken, DiscoveryCandidate, DiscoveryContext, DiscoverySourceScope, double, int, IReadOnlyList, List (+2 more)

### Community 152 - "Cluster 152"
Cohesion: 0.30
Nodes (4): double, IntentScorer, Fact, IntentScorerTests

### Community 153 - "Cluster 153"
Cohesion: 0.29
Nodes (7): CancellationToken, DateTime, Guid, IReadOnlyList, IReadOnlySet, Task, SeriesIntelligenceEngine

### Community 154 - "Cluster 154"
Cohesion: 0.35
Nodes (6): Fact, HttpResponseMessage, HttpStatusCode, string, Task, OrcaMetricsHandlerTests

### Community 155 - "Cluster 155"
Cohesion: 0.22
Nodes (7): Data, Dictionary, Exception, IReadOnlyDictionary, Key, Ok, RecordingMetrics

### Community 156 - "Cluster 156"
Cohesion: 0.18
Nodes (10): FanartImage, Func, IHttpClientFactory, int, List, MetadataCapability, string, FanartImage (+2 more)

### Community 157 - "Cluster 157"
Cohesion: 0.25
Nodes (8): ActionResult, CancellationToken, HttpGet, HttpPost, IEnumerable, string, Task, EmbeddingController

### Community 158 - "Cluster 158"
Cohesion: 0.33
Nodes (4): ScoringWeights, DiscoveryScore, Fact, CandidateScorerTests

### Community 159 - "Cluster 159"
Cohesion: 0.27
Nodes (7): CancellationToken, DateTime, Guid, IReadOnlyList, IReadOnlySet, ISeriesUserStateService, StartedSeries

### Community 160 - "Cluster 160"
Cohesion: 0.47
Nodes (4): Fact, Task, TimeSpan, SingleFlightTests

### Community 161 - "Cluster 161"
Cohesion: 0.35
Nodes (7): CancellationToken, CancellationTokenSource, ILogger, int, Task, TimeSpan, TrailerPrebufferWorker

### Community 162 - "Cluster 162"
Cohesion: 0.22
Nodes (7): DelegatingHandler, CancellationToken, HttpRequestMessage, HttpResponseMessage, string, Task, OrcaMetricsHandler

### Community 163 - "Cluster 163"
Cohesion: 0.33
Nodes (4): TimeZoneInfo, HouseholdClock, Fact, HouseholdClockTests

### Community 164 - "Cluster 164"
Cohesion: 0.27
Nodes (8): CancellationToken, Guid, IReadOnlyList, Task, FunnelReport, FunnelSummary, IFunnelAnalytics, ItemFunnel

### Community 165 - "Cluster 165"
Cohesion: 0.22
Nodes (6): DateTime, IReadOnlyList, EngineAlert, IEngineAlerts, Dictionary, RecordingAlerts

### Community 166 - "Cluster 166"
Cohesion: 0.49
Nodes (3): DiscoverResult, Fact, ContentDocumentTests

### Community 167 - "Cluster 167"
Cohesion: 0.31
Nodes (9): int, List, JellyseerrDetail, JellyseerrDiscoverItem, JellyseerrDiscoverPage, JellyseerrMediaInfo, JellyseerrStatus, JellyseerrUser (+1 more)

### Community 168 - "Cluster 168"
Cohesion: 0.33
Nodes (7): CancellationToken, Guid, IReadOnlyList, Task, BecauseYouWatched, ISimilarityService, SimilarityResult

### Community 169 - "Cluster 169"
Cohesion: 0.36
Nodes (5): CancellationToken, Guid, IProgress, Task, ILibrarySyncService

### Community 171 - "Cluster 171"
Cohesion: 0.31
Nodes (4): Bound, ClientEngine, Port, EngineSettings

### Community 172 - "Cluster 172"
Cohesion: 0.28
Nodes (7): Rating, Score, Votes, double, IEnumerable, Weight, CommunityRatingMath

### Community 173 - "Cluster 173"
Cohesion: 0.25
Nodes (6): TvdbArtwork, TvdbNamed, TvdbTrailer, ImageCandidate, List, TvdbRecord

### Community 174 - "Cluster 174"
Cohesion: 0.25
Nodes (7): ActionResult, CancellationToken, HttpGet, IUserManager, Task, TasteGraphController, IContentVectorIndex

### Community 175 - "Cluster 175"
Cohesion: 0.19
Nodes (8): DateTime, Guid, UserDiscoveryPick, PickRow, CancellationToken, IReadOnlyList, Task, IRowProvider

### Community 176 - "Cluster 176"
Cohesion: 0.31
Nodes (5): Dictionary, CountryLanguages, InlineData, Theory, CountryLanguagesTests

### Community 177 - "Cluster 177"
Cohesion: 0.39
Nodes (5): CancellationToken, DateTime, IReadOnlyList, Task, IArrClient

### Community 178 - "Cluster 178"
Cohesion: 0.25
Nodes (5): string, Daypart, InlineData, Theory, DaypartTests

### Community 179 - "Cluster 179"
Cohesion: 0.47
Nodes (4): CancellationToken, Guid, Task, IPersonalizationService

### Community 180 - "Cluster 180"
Cohesion: 0.32
Nodes (4): GeneratedRegex, Regex, InlineData, Theory

### Community 181 - "Cluster 181"
Cohesion: 0.25
Nodes (6): HealthResponse, ActionResult, AllowAnonymous, HttpGet, HealthController, HealthResponse

### Community 182 - "Cluster 182"
Cohesion: 0.32
Nodes (5): IHostedService, CancellationToken, ILogger, Task, TorrentEngineWarmup

### Community 183 - "Cluster 183"
Cohesion: 0.32
Nodes (5): SelectionResult, double, int, IReadOnlyList, SelectionStage

### Community 185 - "Cluster 185"
Cohesion: 0.46
Nodes (5): CancellationToken, Func, Task, TimeSpan, RowBudget

### Community 186 - "Cluster 186"
Cohesion: 0.32
Nodes (6): CancellationToken, IReadOnlyList, Task, ILlmProvider, LlmMessage, LlmRequestOptions

### Community 187 - "Cluster 187"
Cohesion: 0.32
Nodes (6): CancellationToken, Guid, Task, IRequestService, RequestOutcome, RequestResult

### Community 188 - "Cluster 188"
Cohesion: 0.32
Nodes (4): PortForwardState, StreamSessionHealth, StreamSessionState, SwarmDiagnostics

### Community 190 - "Cluster 190"
Cohesion: 0.43
Nodes (5): Cold, Warm, RowStyle, IReadOnlyDictionary, PendingRow

### Community 191 - "Cluster 191"
Cohesion: 0.33
Nodes (5): CancellationToken, IReadOnlyList, Task, DiscoverySourceScope, IDiscoverySource

### Community 192 - "Cluster 192"
Cohesion: 0.43
Nodes (3): IReadOnlyList, StringBuilder, ContentDocument

### Community 193 - "Cluster 193"
Cohesion: 0.29
Nodes (5): CancellationToken, ILogger, IReadOnlyList, Task, OnnxEmbeddingProvider

### Community 194 - "Cluster 194"
Cohesion: 0.33
Nodes (5): CancellationToken, Guid, IReadOnlyList, Task, IUpcomingProvider

### Community 196 - "Cluster 196"
Cohesion: 0.33
Nodes (5): CancellationToken, DiscoveryCandidate, DiscoveryContext, IReadOnlyList, Task

### Community 197 - "Cluster 197"
Cohesion: 0.33
Nodes (5): CancellationToken, DiscoveryCandidate, DiscoveryContext, IReadOnlyList, Task

### Community 198 - "Cluster 198"
Cohesion: 0.33
Nodes (5): CancellationToken, ILibraryManager, ILogger, Task, JellyfinTrailerSource

### Community 199 - "Cluster 199"
Cohesion: 0.60
Nodes (3): BudgetBytes, Files, UsedBytes

### Community 200 - "Cluster 200"
Cohesion: 0.50
Nodes (3): double, ComingSoonClassifier, ComingSoonKind

### Community 201 - "Cluster 201"
Cohesion: 0.40
Nodes (3): CancellationToken, Task, ICatalogEnricher

### Community 202 - "Cluster 202"
Cohesion: 0.40
Nodes (3): CancellationToken, Task, IWatchProviderEnricher

### Community 203 - "Cluster 203"
Cohesion: 0.40
Nodes (3): CancellationToken, Task, IContentWarningEnricher

### Community 204 - "Cluster 204"
Cohesion: 0.40
Nodes (4): bundle, here, out, safe

### Community 205 - "Cluster 205"
Cohesion: 0.50
Nodes (3): Exception, TimeSpan, ProviderRateLimitedException

### Community 206 - "Cluster 206"
Cohesion: 0.67
Nodes (3): DateTime, Guid, MediaRequest

## Ambiguous Edges - Review These
- `Orca Engine` → `Engine banner artwork`  [AMBIGUOUS]
  docs/assets/engine-banner.svg · relation: illustrated_by

## Knowledge Gaps
- **107 isolated node(s):** `net9.0`, `Jellyfin.Controller (10.11.0)`, `Microsoft.EntityFrameworkCore.Sqlite (9.0.10)`, `Microsoft.NET.Test.Sdk (17.12.0)`, `xunit (2.9.2)` (+102 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **12 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `Orca Engine` and `Engine banner artwork`?**
  _Edge tagged AMBIGUOUS (relation: illustrated_by) - confidence is low._
- **Why does `IEngineMetrics` connect `Community Rating Service` to `Requests and *arr Controllers`, `Cluster 128`, `In-Memory Cache Layer`, `Cluster 130`, `Plugin Bootstrap and Configuration`, `Trailer Scoring Tests`, `Home Render Contract`, `Recommendation Explanation`, `Cluster 140`, `Outbound HTTP and Metrics`, `Trailer State Machine`, `Engine Alerts and Failure Paths`, `Cluster 154`, `Cluster 155`, `Cluster 157`, `TMDB Catalog Enrichment`, `Media Identity Resolution`, `Trailer State Store`, `Cluster 162`, `TMDB HTTP Client`, `Ollama and OpenAI Embeddings`, `LLM History Prompting`, `Cluster 55`, `Cluster 185`, `Cluster 60`, `Cluster 63`, `Cluster 67`, `Cluster 72`, `Cluster 73`, `Cluster 77`, `Cluster 80`, `Cluster 85`, `Cluster 93`, `Cluster 101`, `Cluster 116`, `Cluster 124`?**
  _High betweenness centrality (0.107) - this node is a cross-community bridge._
- **Why does `Wholphin.Engine.Tests` connect `Cluster 70` to `Cluster 97`, `Namespace Fabric (Core)`, `Cluster 104`, `Cluster 103`, `Cluster 135`, `Namespace Fabric (Discovery)`, `Namespace Fabric (Infrastructure)`, `Namespace Fabric (Personalization)`, `Cluster 51`, `Torrent Swarm Scraping`?**
  _High betweenness centrality (0.099) - this node is a cross-community bridge._
- **Why does `IWholphinDbContextFactory` connect `Trailer State Machine` to `Requests and *arr Controllers`, `User Settings API`, `In-Memory Cache Layer`, `Cluster 128`, `Cluster 133`, `Home Render Contract`, `Catalog Metadata Index`, `Cluster 139`, `Genre Graph API`, `Cluster 146`, `Recommendations API`, `Affinity Vector Model`, `Watch History Import`, `Behavior Signal Ingestion`, `TMDB Catalog Enrichment`, `Media Identity Resolution`, `Trailer State Store`, `Community Rating Service`, `Cluster 174`, `Cluster 50`, `Cluster 53`, `Cluster 54`, `Cluster 56`, `Cluster 61`, `Cluster 66`, `Cluster 72`, `Cluster 73`, `Cluster 77`, `Cluster 81`, `Cluster 82`, `Cluster 86`, `Cluster 88`, `Cluster 89`, `Cluster 91`, `Cluster 93`, `Cluster 99`, `Cluster 100`, `Cluster 101`, `Cluster 106`, `Cluster 107`, `Cluster 118`, `Cluster 121`, `Cluster 125`?**
  _High betweenness centrality (0.089) - this node is a cross-community bridge._
- **What connects `net9.0`, `Jellyfin.Controller (10.11.0)`, `Microsoft.EntityFrameworkCore.Sqlite (9.0.10)` to the rest of the system?**
  _107 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Requests and *arr Controllers` be split into smaller, more focused modules?**
  _Cohesion score 0.05048076923076923 - nodes in this community are weakly interconnected._
- **Should `User Settings API` be split into smaller, more focused modules?**
  _Cohesion score 0.07049180327868852 - nodes in this community are weakly interconnected._