# Hybrid Streaming — Backend API

How Orca Engine's torrent source streaming works, and the contract a frontend builds against.

Everything here is served by the Jellyfin plugin at `{jellyfinBaseUrl}/OrcaEngine/…`. There is no
separate service and no separate port.

> **Controller root.** The plugin has shipped under two roots across renames: `/OrcaEngine`
> (current) and `/WholphinEngine` (older). If you need to support both, probe `/OrcaEngine/Health`
> first and fall back — a 404 means "wrong root", any other failure is real.

---

## 1. The model in one picture

```
frontend                          engine                        outside
────────                          ──────                        ───────
GET  /Sources          ─────────▶ Prowlarr search  ──────────▶  indexers
                       ◀─────────  rank + group + cache
POST /Stream/Sessions  ─────────▶ resolve → MonoTorrent  ────▶  peers
                       ◀─────────  prebuffer + ffprobe
GET  /Stream/{t}/file  ◀────────▶ Range ⇄ piece priority  ───▶  peers
```

Two ideas carry the whole design:

**The frontend never sees a torrent.** No magnets, no infohashes, no download URLs. It gets an
opaque source `Id` and, later, an ordinary HTTP URL. Indexer download links embed the server's
Prowlarr API key, so they never cross the API boundary — `DownloadUrl` is `[JsonIgnore]`.

**The player only ever sees HTTP with byte ranges.** A seek is just a new `Range` request, which
re-points the torrent engine's sequential piece picker. Any player that can stream an HTTP URL
works; nothing torrent-aware is needed client-side.

---

## 2. Preconditions

Streaming is **off by default**. All of this must be true or the endpoints behave as if absent:

| Requirement | Where |
|---|---|
| `FeatureSourceStreaming` enabled | Dashboard → Plugins → Orca Engine → *Source streaming* |
| `ProwlarrUrl` + `ProwlarrApiKey` set | same page |
| Jellyfin user is authenticated | standard Jellyfin auth |

**Check before showing any UI:**

```http
GET /OrcaEngine/Settings/Features        (anonymous)
→ 200 { …, "SourceStreaming": true }
```

Treat a missing `SourceStreaming` key as `false` — that means an older plugin build. When it is
false, **render nothing**: the feature should be invisible, not disabled-with-explanation.

> **404 is ambiguous on purpose.** `/Sources` and `/Stream/Sessions` return `404` both when the
> plugin is too old *and* when the feature is switched off. Use the presence of the
> `SourceStreaming` key in `/Settings/Features` to tell those apart.

---

## 3. `GET /OrcaEngine/Sources` — find sources

Authenticated. **Only ever call this from an explicit user action.** Never on browse, scroll,
search-as-you-type, or page load: each call fans out to every configured indexer.

### Query parameters

| Param | Type | Notes |
|---|---|---|
| `title` | string | **Required.** |
| `year` | int | Strongly recommended — stops a remake burying the original. |
| `type` | `movie` \| `tv` | Default `movie`. |
| `season`, `episode` | int | For episode search; builds a `SxxEyy` query. |
| `preferredHeight` | int | Display height (e.g. `1080`, `2160`). Default `1080`. Biases ranking. |

### Responses

| Status | Meaning | Frontend should |
|---|---|---|
| `200` | Ranked results (**may be empty**) | Show groups, or an empty state |
| `400` | `title` missing | Fix the call |
| `401` | Not authenticated | Re-auth |
| `404` | Feature off / plugin too old | Hide the feature |
| `503` | No indexer configured server-side | "Your server isn't set up for this" — distinct from "nothing found" |

### Body

```jsonc
{
  "Recommended":     { /* TorrentSource */ },  // best all-round pick, or null
  "BestQuality":     { … },                    // highest fidelity
  "FastestStart":    { … },                    // most seeders
  "LowestBandwidth": { … },                    // smallest watchable
  "FourKHdr":        { … },                    // best 4K HDR, null if none
  "All":             [ … ]                     // full ranked list, ≤20
}
```

Any group may be `null`. `All` is `[]` when the search ran and genuinely found nothing.

### `TorrentSource`

```jsonc
{
  "Id":      "8cb64724a3d5bac1cba1e9d1",  // opaque handle — pass to /Stream/Sessions
  "Summary": "1080p · Great · EAC3 5.1 · 6.2 GB",
  "Quality": "Great",                      // Excellent | Great | Good | Low | Poor

  // Technical detail — show only behind an "advanced"/"details" affordance
  "Title":            "Movie.2024.1080p.WEB-DL.DDP5.1.x265-GRP",
  "SizeBytes":        6657199308,
  "Seeders":          412,
  "Leechers":         33,
  "Indexer":          "SomeIndexer",
  "ResolutionHeight": 1080,
  "Tier":             "WebDl",   // Unknown|Cam|Hdtv|WebRip|WebDl|BluRay|Remux
  "VideoCodec":       "H.265",   // AV1 | H.265 | H.264 | Legacy | null
  "Hdr":              false,
  "DolbyVision":      false,
  "Audio":            "EAC3 5.1",
  "ReleaseGroup":     "GRP",
  "Score":            82.4       // ordering only; don't display
}
```

`Summary` and `Quality` are pre-composed for display. The engine deliberately produces them so no
frontend has to teach a viewer what a seeder is.

### Ranking behaviour worth designing around

Ranking optimises for **playback actually starting**, not maximum fidelity. Swarm health saturates
at 100 seeders (beyond that the bottleneck is the viewer's connection), so a 60 GB remux with 3
seeders loses to a 6 GB WEB-DL with 900. Resolution above `preferredHeight` is penalised. CAMs and
sub-2-seeder torrents are never returned. Results are deduplicated, so the same release does not
appear in several groups with different ids — **deduplicate your labelled groups by `Id`**, since one
source is often best on multiple axes.

### Caching

Keyed by `(query, preferredHeight)`, shared across all users. Successful searches cache for
`SourceSearchCacheHours` (default 6). **Empty results cache for only 3 minutes** — an empty answer
is usually a failed search rather than a genuine absence, so it must not persist.

---

## 4. `POST /OrcaEngine/Stream/Sessions` — open a stream

Authenticated. **This is slow: budget 10–70 seconds.** It fetches torrent metadata from peers,
pre-buffers the first *and* last piece, then ffprobes the container.

```jsonc
POST /OrcaEngine/Stream/Sessions
{ "SourceId": "8cb64724a3d5bac1cba1e9d1", "Season": null, "Episode": null }
```

Supply **either** `SourceId` (from a prior search — preferred) **or** `Magnet` (a `magnet:` URI, for
direct/debug use; any other scheme is rejected, so this cannot be used to make the server fetch an
arbitrary URL).

### Responses

| Status | Meaning | Frontend should |
|---|---|---|
| `200` | Session open | Play `Path` |
| `400` | Neither field given, or `Magnet` wasn't a `magnet:` URI | Fix the call |
| `410` | `SourceId` expired from cache | "Search again" — then re-search |
| `503` | Couldn't open: dead swarm, no video in torrent, or session cap reached | "That source didn't work — try another", **reopen the list** |

```jsonc
{
  "Token":    "af31…",                                  // capability token
  "Path":     "/OrcaEngine/Stream/af31…/file",           // relative — prepend server base URL
  "FileName": "Movie.2024.1080p.WEB-DL.mkv",
  "Length":   6657199308,
  "MediaInfo": {                                         // null when ffprobe couldn't read it
    "RunTimeTicks": 5328000000,                          // 100ns units (Jellyfin convention)
    "Bitrate":      9990000,
    "Container":    "matroska,webm",
    "Streams": [
      { "Index": 0, "Type": "Video", "Codec": "h264", "Width": 1920, "Height": 1080,
        "Profile": "High", "ColorTransfer": "bt709", "ColorPrimaries": "bt709" },
      { "Index": 1, "Type": "Audio", "Codec": "eac3", "Language": "eng",
        "Title": "Surround 5.1", "Channels": 6, "ChannelLayout": "5.1",
        "IsDefault": true, "IsForced": false },
      { "Index": 3, "Type": "Subtitle", "Codec": "subrip", "Language": "eng", "IsForced": true }
    ]
  }
}
```

**`MediaInfo` may be null** — treat that as normal, not an error. It means ffprobe was unavailable
or the partial file wasn't readable yet; the player should discover tracks from the container
itself. When present, `Index` is the container's own stream index (never renumbered), and all
tracks are embedded (none external).

**HDR:** decide from `ColorTransfer` — `smpte2084` = HDR10, `arib-std-b67` = HLG. Do **not** infer
from `Profile`: "Main 10" only means 10-bit, which plenty of SDR releases use.

### Timeouts your HTTP client must allow

The shared-client default of 30 s **will** cut this off mid-flight. Allow at least 120 s here, and
~120 s for `/Sources` on a cold cache. Server-side, session-open is bounded by
`StreamOpenTimeoutSeconds` (default 60) for the peer phase, so an unreachable swarm fails with a
`503` rather than hanging — but the `.torrent` fetch preceding it adds up to ~30 s more.

---

## 5. `GET /OrcaEngine/Stream/{token}/file` — the stream

**Anonymous**, because a media player generally cannot attach a Jellyfin auth header. The
unguessable `Token` in the URL *is* the authorisation. Note the token is deliberately **not** the
infohash, which is public.

Hand this URL straight to your player. It supports:

- `Accept-Ranges: bytes`
- single ranges: `bytes=N-`, `bytes=N-M`, and suffix `bytes=-N` (players use the last form to read a
  trailing index)
- `206` for ranged requests, `200` for a full read, `416` for an unsatisfiable range
- `HEAD`, returning the same headers with no body — required, or players conclude the stream isn't
  seekable

A read blocks while the covering piece arrives; the player renders that as ordinary buffering.
Seeking into a region with no peers can block until the read times out.

### Closing

```http
DELETE /OrcaEngine/Stream/{token}        (authenticated) → 204
```

Optional. Sessions are evicted after `StreamSessionIdleMinutes` (default 20) with no reads, so
skipping this only means the server seeds a while longer than needed.

---

## 6. Lifecycle and states

A frontend needs these states. Every one has a way forward — none is a dead end.

```
Idle ──▶ Searching ──▶ SourcesFound ──▶ Opening ──▶ Playing
           │              │                │
           ├─▶ NoSources  │                └─▶ Failed(503) ─┐
           └─▶ Unavailable(404/503)                          │
                                          reopen the list ◀──┘
```

| State | Trigger | Recovery offered |
|---|---|---|
| `Searching` | `/Sources` in flight | Indeterminate progress — duration is genuinely unknown, so don't fake a percentage |
| `SourcesFound` | `200`, `All` non-empty | The picker |
| `NoSources` | `200`, `All` empty | "Nothing available yet" + request-to-library instead |
| `Unavailable` | `404` / `503` | "Server isn't set up for this" — no retry, it won't self-fix |
| `Opening` | `POST` in flight | Progress; can take a minute |
| `Failed` | `503` / `410` | Reopen the ranked list so another source can be picked |

---

## 7. Server-side bounds

MonoTorrent runs **in-process inside Jellyfin**, so everything is capped. Relevant to a frontend
because they shape the failures you must handle:

| Setting | Default | Effect you'll see |
|---|---|---|
| `MaxConcurrentStreamSessions` | 3 | 4th concurrent open → `503` |
| `StreamOpenTimeoutSeconds` | 60 | Dead swarm → `503` in ~60 s |
| `StreamSessionIdleMinutes` | 20 | Stream URL 404s after idle teardown |
| `StreamCacheMaxGb` | 20 | Piece cache ceiling |

Sessions are shared: two viewers opening the same source get one torrent session and one set of
peers, and therefore the **same** token.

---

## 8. UX rules the backend assumes

These are enforced by the API's shape, so a frontend that ignores them will fight it:

1. **Never offer sources for media already in the library.** Only surface this when the title is
   genuinely unavailable.
2. **Requesting to the library stays the primary action.** Source streaming is the fallback — the
   automatic pipeline is a better outcome for most viewers.
3. **Search only on explicit intent.** The only call safe to make automatically is
   `/Settings/Features`; it never touches an indexer.
4. **Default to plain language.** Show `Summary`/`Quality`. Keep `Title`, `Seeders`, `Indexer`,
   `Tier`, `VideoCodec` behind an expander.
5. **Offer few choices.** Five labelled picks, deduplicated by `Id`; `All` behind "show everything".

---

## 9. Known gaps

- **Series/episodes are untested.** `season`/`episode` are accepted and the file selector
  understands `SxxEyy`, but nothing has exercised it end to end.
- **The `.torrent`-fetch branch is unexercised.** Indexer links have so far resolved as magnets
  (directly or via a 302). The fetch-and-parse path compiles and is logged distinctly
  ("resolved via fetched .torrent file") but has never run.
- **No progress reporting during `Opening`.** The POST is a single blocking call, so a frontend
  cannot show real progress. Splitting it into create-then-poll is the fix if that matters.
- **Nothing is imported into the library.** Streaming and downloading are separate concepts; a
  "keep this" feature does not exist yet.
