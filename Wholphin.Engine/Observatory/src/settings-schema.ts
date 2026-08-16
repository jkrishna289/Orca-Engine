/**
 * Describes the engine's admin configuration for the settings form.
 *
 * A table rather than 55 hand-written inputs: adding a field here is one line, and the renderer
 * stays generic. Anything present in the server's configuration but absent from this table is still
 * shown, in an "Other settings" group inferred from its value type — so a field added to
 * PluginConfiguration.cs can never silently become uneditable just because this file wasn't updated.
 */

export type FieldKind = 'bool' | 'int' | 'float' | 'text' | 'secret' | 'select';

export interface Field {
  key: string;
  label: string;
  kind: FieldKind;
  help?: string;
  min?: number;
  max?: number;
  step?: number;
  placeholder?: string;
  options?: { value: string; label: string }[];
}

export interface Group {
  title: string;
  blurb?: string;
  fields: Field[];
}

/** The plugin's own id — the Jellyfin configuration API is keyed on it. */
export const PLUGIN_ID = 'b9f3c2e1-7a4d-4e6b-9c8f-1d2a3b4c5d6e';

export const GROUPS: Group[] = [
  {
    title: 'Core',
    blurb: 'The kill switch and the shape of a home page.',
    fields: [
      { key: 'Enabled', label: 'Engine enabled', kind: 'bool', help: 'Global kill switch. Off means the client falls back to its own home.' },
      { key: 'RefreshIntervalMinutes', label: 'Refresh interval', kind: 'int', min: 1, max: 1440, help: 'Minutes between precomputed read-model refreshes.' },
      { key: 'DefaultRowSize', label: 'Default row size', kind: 'int', min: 1, max: 100, help: 'Items per row when the client does not ask for a size.' },
      { key: 'SpotlightCount', label: 'Spotlight billboard items', kind: 'int', min: 0, max: 30, help: 'Titles in the rotating hero billboard at the top of the home.' },
      { key: 'SpotlightShowcaseCount', label: 'Spotlight showcase rows', kind: 'int', min: 0, max: 10, help: 'Full-width cinematic single-title rows placed deep in the feed. 0 disables them.' },
      { key: 'BehaviorRetentionDays', label: 'Behavior retention (days)', kind: 'int', min: 0, max: 3650, help: 'How long raw behavior events are kept. 0 keeps everything, which grows without bound.' },
      { key: 'HouseholdTimeZone', label: 'Household time zone', kind: 'text', placeholder: 'e.g. Asia/Kolkata — empty uses the server local zone', help: 'Used for daypart bucketing, so a signal is scored under the daypart it was learned in.' },
    ],
  },
  {
    title: 'Home rows',
    blurb: 'Which rows the engine is allowed to emit. Turning one off removes it everywhere.',
    fields: [
      { key: 'FeaturePersonalization', label: 'Personalization', kind: 'bool', help: 'The For You row and personalized ordering. Off makes every home identical.' },
      { key: 'FeatureSpotlight', label: 'Spotlight billboard', kind: 'bool' },
      { key: 'FeatureSpotlightShowcase', label: 'Spotlight showcase', kind: 'bool', help: 'Cinematic single-title rows that play a trailer inline once focused.' },
      { key: 'FeatureContinueWatching', label: 'Continue watching', kind: 'bool' },
      { key: 'FeatureTrending', label: 'Trending', kind: 'bool' },
      { key: 'FeatureSimilarityRows', label: 'Because you watched', kind: 'bool' },
      { key: 'FeatureExploration', label: 'Exploration', kind: 'bool', help: 'Deliberately novel picks, so recommendations do not collapse onto one taste.' },
      { key: 'FeatureNewSinceAway', label: 'New since you were away', kind: 'bool' },
      { key: 'FeatureComingSoon', label: 'Coming soon', kind: 'bool', help: 'Needs Sonarr or Radarr for the calendar.' },
      { key: 'FeatureContinueTheStory', label: 'Continue the story', kind: 'bool', help: 'Skipped seasons and abandoned series.' },
      { key: 'FeatureMoodCollections', label: 'Mood collections', kind: 'bool' },
      { key: 'FeatureContentWarnings', label: 'Content advisories', kind: 'bool', help: 'LLM-generated. Needs an LLM key.' },
      { key: 'FeatureRequests', label: 'Media requests', kind: 'bool', help: 'Lets the client request titles through Jellyseerr.' },
      { key: 'FeatureTasteDiscovery', label: 'Taste discovery', kind: 'bool', help: 'Per-user justified TMDB pulls plus trending and country rows. Needs a TMDB key.' },
    ],
  },
  {
    title: 'Discovery tuning',
    blurb: 'How aggressively the engine looks beyond your library. Defaults are the designed behaviour.',
    fields: [
      { key: 'DiscoveryMinConfidence', label: 'Minimum profile confidence', kind: 'float', min: 0, max: 1, step: 0.05, help: 'Below this, a user gets no taste pull — too little history to be worth guessing from.' },
      { key: 'DiscoveryMaxImportsPerPull', label: 'Max imports per pull', kind: 'int', min: 1, max: 200 },
      { key: 'DiscoveryPickTtlDays', label: 'Pick lifetime (days)', kind: 'int', min: 1, max: 90, help: 'Seeded picks live twice as long.' },
      { key: 'DiscoveryMaxUsersPerCycle', label: 'Users per cycle', kind: 'int', min: 1, max: 100, help: 'Rotation size — users are pulled oldest-first.' },
      { key: 'DiscoveryExplorationFraction', label: 'Exploration share', kind: 'float', min: 0, max: 0.5, step: 0.05, help: 'Share of picks reserved for labelled exploration.' },
      { key: 'DiscoveryTrendingTasteWeight', label: 'Trending taste weight', kind: 'float', min: 0, max: 1, step: 0.05, help: '0 is pure popularity; higher flavours trending with the viewer\'s taste.' },
    ],
  },
  {
    title: 'Ranking weights',
    blurb: 'Advanced. These sum into the final score — changing one changes the balance of all of them.',
    fields: [
      { key: 'WeightRecPersonalization', label: 'For You · personalization', kind: 'float', min: 0, max: 1, step: 0.05 },
      { key: 'WeightRecQuality', label: 'For You · quality', kind: 'float', min: 0, max: 1, step: 0.05 },
      { key: 'WeightRecRecency', label: 'For You · recency', kind: 'float', min: 0, max: 1, step: 0.05 },
      { key: 'WeightRecAvailability', label: 'For You · availability', kind: 'float', min: 0, max: 1, step: 0.05 },
      { key: 'WeightDiscTaste', label: 'Discovery · taste', kind: 'float', min: 0, max: 1, step: 0.05 },
      { key: 'WeightDiscPopularity', label: 'Discovery · popularity', kind: 'float', min: 0, max: 1, step: 0.05 },
      { key: 'WeightDiscFreshness', label: 'Discovery · freshness', kind: 'float', min: 0, max: 1, step: 0.05 },
      { key: 'WeightDiscNovelty', label: 'Discovery · novelty', kind: 'float', min: 0, max: 1, step: 0.05 },
      { key: 'WeightDiscSourceConfidence', label: 'Discovery · source confidence', kind: 'float', min: 0, max: 1, step: 0.05 },
    ],
  },
  {
    title: 'Integrations',
    blurb: 'Leave a URL empty to disable that integration entirely.',
    fields: [
      { key: 'TmdbApiKey', label: 'TMDB API key (v3)', kind: 'secret', help: 'The single most useful key here — artwork, trailers, discovery and watch providers all need it.' },
      { key: 'WatchProviderRegion', label: 'Watch provider region', kind: 'text', placeholder: 'US', help: 'ISO-3166-1 country code used to resolve streaming-provider branding.' },
      { key: 'JellyseerrUrl', label: 'Jellyseerr URL', kind: 'text', placeholder: 'http://localhost:5055' },
      { key: 'JellyseerrApiKey', label: 'Jellyseerr API key', kind: 'secret' },
      { key: 'SonarrUrl', label: 'Sonarr URL', kind: 'text', placeholder: 'http://localhost:8989' },
      { key: 'SonarrApiKey', label: 'Sonarr API key', kind: 'secret' },
      { key: 'RadarrUrl', label: 'Radarr URL', kind: 'text', placeholder: 'http://localhost:7878' },
      { key: 'RadarrApiKey', label: 'Radarr API key', kind: 'secret' },
    ],
  },
  {
    title: 'Trailers',
    blurb: 'Where trailer URLs come from, and which videos are acceptable. Sources are tried in the order listed until one answers.',
    fields: [
      {
        key: 'TrailerSourceOrder', label: 'Source order', kind: 'text',
        placeholder: 'tmdb,jellyfin,stored',
        help: 'Comma-separated. tmdb = TMDB videos (language-aware). jellyfin = trailer URLs Jellyfin already found for a library item — free, no key, but library items only. stored = the URL saved on the catalog row. search = ask yt-dlp to search YouTube by title and year; powerful, but it can return a fan edit or a review, so it is off by default. Remove a token to disable that source; unknown tokens are ignored.',
      },
      { key: 'TrailerLanguage', label: 'Preferred language', kind: 'text', placeholder: 'en', help: 'ISO 639-1. A matching-language trailer wins whenever one exists; otherwise the title\'s native trailer is used.' },
      { key: 'TrailerAllowTeasers', label: 'Allow teasers', kind: 'bool', help: 'Use a teaser when no full trailer exists. Off means such titles simply get no trailer.' },
      { key: 'TrailerRequireOfficial', label: 'Require official', kind: 'bool', help: 'Official videos are always preferred; this makes them mandatory, which reduces coverage.' },
      { key: 'TrailerAllowNonYouTube', label: 'Allow non-YouTube', kind: 'bool', help: 'Accept Vimeo and similar. yt-dlp can fetch them, but they are rarer and less reliably the real trailer.' },
      { key: 'FeatureTrailerPrebuffer', label: 'Prebuffer trailers', kind: 'bool', help: 'Download and transcode ahead of time. Needs yt-dlp and ffmpeg on the server.' },
    ],
  },
  {
    title: 'Torrent streaming',
    blurb: 'Off by default: peer traffic comes from this server\'s IP, so switching it on is an operator decision. The Torrent Streaming tab shows what these settings are actually doing.',
    fields: [
      { key: 'FeatureSourceStreaming', label: 'Source streaming', kind: 'bool', help: 'Master switch. Off means the endpoints 404 and the app hides the feature entirely.' },
      { key: 'ProwlarrUrl', label: 'Prowlarr URL', kind: 'text', placeholder: 'http://localhost:9696', help: 'Where sources are searched. Empty disables discovery. Only public indexers are ever used — private-tracker results are dropped, because streaming reads scattered pieces and stops when the viewer does, which gets accounts banned.' },
      { key: 'ProwlarrApiKey', label: 'Prowlarr API key', kind: 'secret' },
      { key: 'SourceSearchCacheHours', label: 'Search cache (hours)', kind: 'int', min: 1, max: 168, help: 'Shared across users. Empty results are cached for only 3 minutes regardless.' },
      { key: 'StreamCachePath', label: 'Piece cache folder', kind: 'text', placeholder: 'empty uses orca-streams under the plugin data path', help: 'Needs room for whole media files while streaming.' },
      { key: 'StreamCacheMaxGb', label: 'Piece cache cap (GB)', kind: 'int', min: 1, max: 2000 },
      { key: 'StreamLibraryPath', label: 'Keep folder', kind: 'text', placeholder: 'empty disables keeping', help: 'Must be inside a Jellyfin library or the scan will never find what is copied there. Empty is the only safe default.' },
      { key: 'MaxConcurrentStreamSessions', label: 'Concurrent sessions', kind: 'int', min: 1, max: 20 },
      { key: 'StreamSessionIdleMinutes', label: 'Idle timeout (minutes)', kind: 'int', min: 1, max: 240, help: 'How long a session with no reads survives.' },
      { key: 'StreamOpenTimeoutSeconds', label: 'Open timeout (seconds)', kind: 'int', min: 0, max: 600, help: '0 means none, which is the default: a popular torrent can take minutes to find its first peer, and a deadline used to report those as dead.' },
    ],
  },
  {
    title: 'Peer discovery and privacy',
    blurb: 'How the engine finds peers, and what it reveals doing so. Defaults match the behaviour that was hardcoded before these were editable.',
    fields: [
      { key: 'StreamEnableDht', label: 'Enable DHT', kind: 'bool', help: 'Finds peers without a tracker. Honest caveat: MonoTorrent\'s DHT has never bootstrapped on this server — the Torrent Streaming tab reports its real state rather than assuming this switch delivers.' },
      { key: 'StreamEnablePeerExchange', label: 'Enable peer exchange (PeX)', kind: 'bool', help: 'Lets connected peers introduce the peers they know. Costs no extra connection attempts.' },
      { key: 'StreamEnableLocalPeerDiscovery', label: 'Enable local peer discovery', kind: 'bool', help: 'Finds peers on your own network for free — but announces to the LAN what this server is downloading.' },
      {
        key: 'StreamEncryptionMode', label: 'Encryption mode', kind: 'select',
        options: [
          { value: 'allow', label: 'Allow encryption (prefer, accept plaintext)' },
          { value: 'require', label: 'Require encryption' },
          { value: 'disable', label: 'Disable encryption' },
        ],
        help: 'Require is more private and strictly shrinks the usable swarm, since unencrypted peers are refused.',
      },
    ],
  },
  {
    title: 'Connectivity and port forwarding',
    blurb: 'Inbound peers are the one source of extra swarm capacity that costs no outbound connection attempt. Making this server reachable is the most effective change available here — and the Torrent Streaming tab will tell you whether it worked.',
    fields: [
      { key: 'StreamListenPort', label: 'Inbound port', kind: 'int', min: 0, max: 65535, help: 'The local port peers connect in on. 0 picks a random one every restart, which means any router forward is stale immediately. Set a fixed high port (51413 is conventional) and forward it, and peers can reach you instead of only the other way round.' },
      { key: 'StreamAllowPortForwarding', label: 'Ask the router (UPnP/NAT-PMP)', kind: 'bool', help: 'Fails soft: if the router refuses, the engine carries on outbound-only. A created mapping means the router agreed — not that a peer got through, which only the reachability check can show.' },
    ],
  },
  {
    title: 'Advanced networking',
    blurb: 'Leave both of these empty unless you know you need them. The inbound port above is the local endpoint peers connect to; these override the external endpoint advertised to trackers, and are only needed when the two genuinely differ — a manual forward to a different external port, or a host that cannot observe its own public address. Setting them wrongly advertises an endpoint nothing can reach. Both are required together, or neither applies.',
    fields: [
      { key: 'StreamReportedAddress', label: 'Advertised address', kind: 'text', placeholder: 'usually empty', help: 'External IP peers should use. Ignored unless the advertised port is also set.' },
      { key: 'StreamReportedPort', label: 'Advertised port', kind: 'int', min: 0, max: 65535, help: 'External port peers should use. Ignored unless the advertised address is also set.' },
    ],
  },
  {
    title: 'Peer budget',
    blurb: 'A budget against your router, not a speed control. Read the warning on the half-open field before changing anything here.',
    fields: [
      { key: 'StreamMaxConnections', label: 'Max connections', kind: 'int', min: 1, max: 500, help: 'Total simultaneous peer connections across all sessions.' },
      { key: 'StreamMaxHalfOpenConnections', label: 'Max connection attempts in flight', kind: 'int', min: 1, max: 50, help: 'WARNING: raising this above 8 has twice taken the whole LAN offline — the August incident, and again on 2026-08-13, which reset an SSH session to the server mid-test. An unanswered connection attempt holds a slot in the router\'s NAT table for two minutes, so what loads the router is the rate of new attempts. Forward a port first: inbound peers add swarm capacity without a single outbound attempt.' },
      { key: 'StreamConnectionTimeoutSeconds', label: 'Connection timeout (seconds)', kind: 'int', min: 1, max: 60, help: 'Lowering this raises the attempt rate for the same in-flight count, so it pushes on the router in the same way the field above does.' },
      { key: 'StreamMaxDownloadRateKbps', label: 'Download cap (KiB/s)', kind: 'int', min: 0, max: 1000000, help: '0 is unlimited. A cap below the media bitrate guarantees stalls.' },
      { key: 'StreamMaxUploadRateKbps', label: 'Upload cap (KiB/s)', kind: 'int', min: 0, max: 1000000, help: '0 is unlimited, and usually right — many peers reciprocate on upload rate, so throttling it tends to cost download speed on exactly the swarms that are already slow.' },
    ],
  },
  {
    title: 'AI',
    blurb: 'Any OpenAI-compatible endpoint. Every AI feature fails soft — if the call fails, the engine\'s own ranking is used instead.',
    fields: [
      { key: 'LlmBaseUrl', label: 'LLM base URL', kind: 'text', placeholder: 'https://api.groq.com/openai/v1', help: 'Empty uses Groq. Use http://localhost:11434/v1 for a local Ollama.' },
      { key: 'LlmApiKey', label: 'LLM API key', kind: 'secret', help: 'Empty falls back to the Groq key below. Local endpoints often need no key at all.' },
      { key: 'LlmModel', label: 'LLM model', kind: 'text', placeholder: 'llama-3.3-70b-versatile', help: 'Empty falls back to the Groq model below.' },
      { key: 'FeatureLlmRerank', label: 'LLM re-ranking', kind: 'bool', help: 'Reorders and annotates the For You row.' },
      { key: 'FeatureLlmCuration', label: 'LLM curation', kind: 'bool', help: 'Lets the LLM select from a wide pool rather than only reordering. Costs more calls.' },
      { key: 'FeatureLlmDiscovery', label: 'LLM discovery', kind: 'bool', help: 'Generates external picks from watch history. Needs a TMDB key too, to resolve titles. Surfaces rows only — never requests content.' },
      { key: 'LlmDiscoveryMaxPerMediaType', label: 'Discovery picks per call', kind: 'int', min: 1, max: 25 },
      { key: 'LlmDiscoveryHistoryCap', label: 'History titles per call', kind: 'int', min: 5, max: 40 },
      { key: 'GroqApiKey', label: 'Groq API key (legacy)', kind: 'secret', help: 'Kept so existing installs keep working. Prefer the LLM fields above.' },
      { key: 'GroqModel', label: 'Groq model (legacy)', kind: 'text', placeholder: 'llama-3.3-70b-versatile' },
    ],
  },
  {
    title: 'Embeddings',
    blurb: 'Content vectors for similarity. The default is local and needs no key; the others fall back to it if unconfigured or failing.',
    fields: [
      {
        key: 'EmbeddingProvider', label: 'Provider', kind: 'select',
        options: [
          { value: 'tfidf', label: 'TF-IDF (local, no key)' },
          { value: 'openai', label: 'OpenAI' },
          { value: 'gemini', label: 'Google Gemini' },
          { value: 'voyage', label: 'Voyage AI' },
          { value: 'jina', label: 'Jina' },
          { value: 'onnx', label: 'ONNX (local model file)' },
        ],
      },
      { key: 'OpenAiApiKey', label: 'OpenAI key', kind: 'secret' },
      { key: 'OpenAiEmbeddingModel', label: 'OpenAI model', kind: 'text', placeholder: 'text-embedding-3-small' },
      { key: 'GeminiApiKey', label: 'Gemini key', kind: 'secret' },
      { key: 'GeminiEmbeddingModel', label: 'Gemini model', kind: 'text', placeholder: 'gemini-embedding-001' },
      { key: 'VoyageApiKey', label: 'Voyage key', kind: 'secret' },
      { key: 'VoyageEmbeddingModel', label: 'Voyage model', kind: 'text', placeholder: 'voyage-3.5' },
      { key: 'JinaApiKey', label: 'Jina key', kind: 'secret' },
      { key: 'JinaEmbeddingModel', label: 'Jina model', kind: 'text', placeholder: 'jina-embeddings-v3' },
      { key: 'OnnxModelPath', label: 'ONNX model path', kind: 'text', placeholder: '/config/models/model.onnx' },
    ],
  },
];

/** Keys this table knows about — everything else is rendered generically. */
export const KNOWN_KEYS = new Set(GROUPS.flatMap((g) => g.fields.map((f) => f.key)));

/** Best-guess field description for a configuration key this table does not cover. */
export function inferField(key: string, value: unknown): Field {
  const secret = /key|token|password|secret/i.test(key);
  if (typeof value === 'boolean') return { key, label: key, kind: 'bool' };
  if (typeof value === 'number') return { key, label: key, kind: Number.isInteger(value) ? 'int' : 'float' };
  return { key, label: key, kind: secret ? 'secret' : 'text' };
}
