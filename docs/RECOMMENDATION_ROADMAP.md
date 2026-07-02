# Wholphin Engine — Recommendation System Roadmap

*Architecture & planning document. Source brief: `Recommendation FEA.md` (features A–L).*
*Status as of 2026-06-22. The engine is a Jellyfin plugin (Tier 1: in-process, SQLite + IMemoryCache, ports & adapters).*

---

## 1. Where we are

**Recommendation V1 (the foundation — built & proven):**

```
Behavior events → Affinity vectors → Content-based scoring → Ranking → Home generation
```

- Content-first hybrid recommender (collaborative filtering deliberately omitted — too few users to be anything but cold).
- 3-stage pipeline: candidate gen → weighted ranking (`0.60·Personalization + 0.15·Quality + 0.05·Recency + 0.10·Availability + 0.10·Trending`) → diversity re-rank.
- **Confidence gating** (CS 0–1 by event density): below ~0.40 the engine leans on global priors; above it, personal vectors dominate.
- Server-driven dynamic cards, capability-gated.

**Already delivered beyond V1 (this milestone cycle):**

| Capability | Feature | State |
|---|---|---|
| Layered settings + feature flags (system→admin→group→user) | — | ✅ M1 #8 |
| Roaming per-user settings store | — | ✅ M1 #8 |
| Jellyseerr port/adapter (live-verified, read-only) | I/L | ✅ M2 |
| Engine-proxied requests + request affinity | **I** | ✅ M2 (core) |
| Availability state machine + reconciliation worker | **L** | ✅ M2 (core) |
| Discovery import + "Worth Requesting" row | **L** | ✅ M2 (core) |
| Item similarity graph ("More Like This" / "Because You Watched X") | **G** | ✅ M2.x |

---

## 2. Feature roadmap (A–L)

| F | Feature | Milestone | Value | Risk | Schema cost | Status |
|---|---|---|---|---|---|---|
| **G** | Item Similarity Graph | M2.x | Extremely High | Low | None (on-demand+cache) | ✅ Done |
| **I** | Jellyseerr Request Learning | M2 | High | Low | None | ◑ Core done; weights expandable |
| **L** | Availability-Aware Discovery | M2 | Differentiator | Low | None | ◑ Core done; more rows pending |
| **J** | Confidence-Driven Home Layout | M2.x | Very High | Low | None | ✅ Done |
| **C** | Context-Aware (daypart) Personalization | M2.x | Very High | Low | None (JSON sub-vectors) | ✅ Done |
| **H** | People Affinity (actor/director/writer) | M3 | Extremely High | Medium | None (column exists) | ✅ Done (resync to backfill) |
| **D** | Session-Based Recommendations | M3 | Very High | Low–Med | None (cache) | ✅ Done |
| **A** | Home Dwell-Time Tracking | M3 | Very High | Medium | None (reuse BehaviorEvent) | ⬜ Needs app telemetry |
| **E** | Negative Signal Learning | M3 | High | Medium | None | ◑ Partial; expands after A |
| **B** | Impression Funnel Analytics | M3 | High | Medium | New metrics table | ⬜ After A |
| **F** | Genre Relationship Graph | M3 | Medium-High | Low | Singleton JSON / cache | ✅ Done |
| **K** | Exploration Engine | M3 | High | Medium | None | ◑ Diversity re-rank exists |

**Guiding constraint:** until an EF-migration strategy lands, prefer **JSON sub-fields on existing entities** and **cache** over new tables/columns, so the plugin stays deploy-as-DLL-only. (B is the one feature that genuinely wants a new table — schedule it once migrations exist.)

---

## 3. Per-feature architecture impact

**C — Context-Aware (daypart) Personalization.** Behavior events already store `hour`/`dow` in `ContextJson`. Compute four daypart-bucketed affinity sub-vectors and store them inside the existing `UserProfile.AffinityJson` (no schema change). At scoring time, blend the active daypart's vector with the all-time vector. *Components:* PersonalizationService, recommender. *Worker:* existing recompute. *Cache:* home key should include the daypart bucket.

**D — Session-Based Recommendations.** Build a transient session vector from the last 5–10 watched items; blend `0.70·long-term + 0.30·session`. *Storage:* in-memory/cache keyed by user+session, short TTL. *Components:* a `SessionService` + recommender hook. *No schema.*

**H — People Affinity.** Populate `CatalogItem.PeopleJson` in `CatalogMapper` (needs `DtoOptions` to include people; watch sync cost), add a `Person` dimension to the affinity vector + similarity scorer. The entity column already exists. *Components:* CatalogMapper, PersonalizationService, SimilarityScorer, CatalogFeatures. *Worker:* recompute + a resync to backfill people.

**J — Confidence-Driven Home Layout.** Today confidence affects *ranking*; extend it to *page composition* in `HomeService`: low confidence → lead with Trending/Popular/Top-Rated; high confidence → lead with For You / Because You Watched / Similar. *Components:* HomeService only. *No schema, no new service.* Pairs naturally with the existing feature-flag/layout system.

**A — Home Dwell-Time Tracking.** New `BehaviorEventType`s (CardImpression, CardFocused with focus-duration in `ContextJson`); a **batch** capture endpoint (telemetry volume); light weights so dwell nudges, not dominates. *Requires app cooperation.* Reuses `BehaviorEvent` — no new table. Unlocks B and strengthens E.

**E — Negative Signal Learning.** Expand beyond the existing ThumbsDown-excludes + early-abandon penalty: repeated-impression-without-click, decay model for stale dislikes. Depends on A's impression signals. *Reuses behavior log.*

**B — Impression Funnel Analytics.** Aggregate shown→focused→clicked→played→completed per item → CTR / focus-rate / completion-rate. *Best as a precomputed metrics table* (the one new-table case) refreshed by a worker; feeds the Trending/Quality signals. *Schedule after migrations exist.*

**F — Genre Relationship Graph.** Offline co-occurrence over catalog genres → adjacency (Crime→Thriller→Mystery). Small payload: a singleton JSON row or cache entry, regenerated by a background worker post-sync. Feeds candidate expansion + similarity.

**K — Exploration Engine.** ε-greedy injection (~10%) of diverse/novel candidates into the ranked set; the recommender already has a diversity (MMR-lite) re-rank to build on. Add satisfaction safeguards (cap exploration in low-confidence states).

**I / L — already core-built; expansions:** I → richer request weights (RequestApproved/Fulfilled already in the enum); L → more rows (Available Now / Recently Added / Coming Soon) + request-probability scoring.

**Milestone 8 — Discovery & Metadata (engine, ✅ built; build-ahead):** the engine half of the amended
FEA. (1) **TMDB client extended** — keywords/credits/recommendations/similar + `next_episode_to_air`;
enrichment now also backfills tags (keywords) + people (credits) onto requestable rows. (2) **`IArrClient`**
fail-soft Sonarr/Radarr calendar port (config: Sonarr/Radarr URL+key). (3) **Schema v4** — `WholphinRating`/
`WholphinVotes` columns + per-user last-seen via `UserSetting` (key `meta.lastSeenUtc`). (4) **F2 New Since
You Were Away** (`INewSinceProvider`: catalog DateAdded + *arr recent since last-seen; last-seen stamped on
`/Bootstrap`). (5) **F3 Coming Soon** (`IUpcomingProvider`: *arr calendar primary → TMDB next-episode
fallback, cached 12h). (6) **F4 Did You Know** (`ITriviaProvider`: Groq-first → TMDB-keyword fallback,
cached; `/Metadata/Trivia`). (7) **F6 Mood Collections** (`MoodCollections` rule map + service, rotated
daily). (8) **F7 Community Ratings** (`CommunityRatingMath` Bayesian aggregate → catalog columns;
`/Analytics/CommunityRating`; recomputed each maintenance tick). (9) **F1 reason chips**
(`RecommendationReasons`: deterministic Similar Genre / Shared Actor / Highly Rated → REASON badges on
For You; Groq "why" stays additive). (10) **F8 Popularity** (`IPopularityService`: Most Watched/Completed/
Requested/Rewatched/Dropped; `/Analytics/Popularity`). New flags `NewSinceAway`/`ComingSoon`/
`MoodCollections` wired through all layers. F5/F12 were already done (similarity scorer + the engine-as-service).
**Phases 2 (app polish: top-nav bug #8, focus memory, instant details) + 3 (F11 smart trailers, yt-dlp/ffmpeg)
are planned next.** Pure logic unit-verified (22/22): Bayesian rating math, mood predicates, reason
derivation. NOT yet deployed.

**L — TMDB direct (Milestone 7, ✅ built):** the second hybrid discovery source. A fail-soft `ITmdbClient` (raw HTTP, v3 key or v4 token) adds (1) **metadata enrichment** — genre names + poster/backdrop artwork + trailer URL backfilled onto requestable rows that lacked them, so "Worth Requesting" cards finally show real art and request affinity/similarity get genre signal for not-yet-available titles; and (2) **TMDB-direct trending/popular discovery** (gated by the new `TmdbDiscovery` flag), so discovery works without Jellyseerr. New `CatalogItem` columns `PosterImageUrl`/`BackdropImageUrl`/`TrailerUrl` (schema v3 via the migration runner). `DefaultCardSelector` surfaces the stored artwork on cards whose items have no Jellyfin id. `TmdbEnricher` runs each maintenance tick (round-robin, no-op when unconfigured). The TMDB trailer URL is stored as data (YouTube watch URL) but not fed to the billboard's `TrailerStreamUrl` — it isn't a directly-playable stream, so inline trailers for requestable titles still await a YouTube-extractor path on the app.

---

## 4. Dependency graph

```
Behavior Engine ✅ ──┬─→ Affinity Vectors ✅ ─┬─→ Recommender V1 ✅ ─┬─→ Home Generation ✅
                     │                         │                      │
                     │                         ├─→ C Daypart ─────────┤
                     │                         ├─→ D Session ─────────┤
                     │                         └─→ J Confidence layout ┘
                     │
Catalog ✅ ──┬─→ G Similarity ✅
             ├─→ F Genre graph ──→ (candidate expansion)
             └─→ H People affinity  (needs People metadata populated)

App telemetry ──→ A Dwell/Impression ──┬─→ B Funnel analytics
                                        └─→ E Negative learning

Jellyseerr ✅ ──┬─→ I Request learning ✅(core)
                └─→ L Availability discovery ✅(core)
```

Key reads: **B depends on A**; **E strengthened by A**; **H depends on People metadata** (a catalog-sync change); G/C/D/J depend only on already-built layers.

---

## 5. Recommended build order

Ordered to maximize recommendation-quality gain per unit of risk, front-loading the cheap/no-schema/no-app-dependency wins:

1. **J — Confidence-driven layout** — trivial (HomeService only), high perceived quality, no deps.
2. **C — Daypart context** — JSON-only, very high value, reuses recompute worker.
3. **H — People affinity** — extremely high value; the one cost is populating People in sync (+ a backfill resync).
4. **D — Session recs** — high value, in-memory/cache, no schema.
5. **I / L expansions** — small deltas on already-built integrations.
6. **A — Dwell/impression telemetry** — unlocks the analytics tier; gated on app work.
7. **E — Negative learning** → **B — Funnel analytics** (after A; B when migrations exist).
8. **F — Genre graph**, then **K — Exploration** — quality polish once the signal base is rich.

---

## 6. Long-term vision: V1 → V4

- **V1 (done):** content-based hybrid, confidence-gated, single-user. Tier 1 (SQLite + IMemoryCache).
- **V2 (in progress now):** + similarity (G), people affinity (H), daypart context (C), session (D), confidence-driven layout (J), request learning (I), availability-aware discovery (L). *"Rich content-based, context- and session-aware."* Still fits Tier 1.
- **V3:** + telemetry-driven signals — dwell/impression (A), funnel analytics (B), negative learning (E) — plus genre graph (F) and exploration (K). Higher event volume favors **Tier 2** (Postgres + pgvector + Redis; ClickHouse optional for analytics). Introduce on-box embeddings behind the existing feature/port seams.
- **V4:** + collaborative filtering (once the user base is large enough to be warm), pgvector similarity replacing the on-demand scorer, and an **optional Stage-3 LLM re-ranker / row-title + "why" generator** (provider a config switch via `ILlmProvider` — never computes base recs). Tier 2/3, cross-device session continuity.

Every step stays compatible with the current ports-&-adapters architecture and the Jellyfin-plugin constraints: features graduate tiers by swapping adapters (cache→Redis, SQLite→Postgres, local scoring→pgvector), not by rewrites.
