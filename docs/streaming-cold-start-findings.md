# Torrent streaming: cold-start findings, 2026-08-16

What the server log says about a real playback attempt, why it took over six minutes to show a
frame, and what I propose to change. Every number here was measured on the live Debian box — none is
estimated.

---

## 1. How this was measured

The build deployed on 2026-08-15 recorded session *preparation* in detail and then went silent.
Jellyfin runs with no HTTP request logging (`grep -c 'OrcaEngine/Stream'` across every log file =
`0`), so the player's byte-range reads left no trace at all: a read that blocked eight seconds and
one that returned instantly looked identical.

Four log lines were added to fix that, all in `Wholphin.Engine/Streaming/TorrentStreamService.cs`:

| Line | Fires |
|---|---|
| `session {Id} ready — {File} ({Bytes} bytes, {Bitrate} bps)` | preparation ends |
| `session {Id} first frame — {Ms}ms after ready …` | the player's first read |
| `session {Id} read at offset {Offset} blocked {Ms}ms …` | any read blocking ≥ 3 s |
| `session {Id} playing — {Rate} B/s to the player over {Sec}s …` | every minute, only while being read |

Three touch points, because every read funnels through one method:

- `StreamSession` gained timing fields (`ReadyAt`, `FirstReadAt`, `BytesDelivered`, `Stalls`,
  `WorstStallMs`, plus two heartbeat snapshot fields).
- `ReadAsync` — the single funnel every player read passes through — times the blocking await.
- The heartbeat rides the **existing** 1-minute idle-sweep `Timer` and reuses the existing
  `ReadHealth()`. No new timer, no new measurement code.

Deployed 12:22, clean start, 461 catalog items, `dotnet build -c Release` 0 warnings / 0 errors,
316/316 tests passing.

### A defect in the instrumentation itself

The first run reported `first frame — -1ms after ready`, which is nonsense. Cause: `ffprobe` reads
through the *same* funnel while the session is still `Preparing` — it pulls 12 MB of head and then
seeks to the tail — so the probe's first read claimed the first-frame line, and `ReadyAt` was still
null, hence `-1`.

Fixed: delivery accounting and the first-frame line are now scoped to `State == Ready`. Stall
warnings stay unconditional, because a probe read that blocked 16 seconds is the single best
explanation for why the wait was long.

Also added, because its absence made the diagnosis ambiguous: `OpenConnections` on
`StreamSessionHealth`, so the heartbeat now prints `conns[N open of M avail, S seed]`. `Peers`
counts what the trackers *reported*; only open connections say what is actually being downloaded
from. Both fixes are built (0/0) and **not yet deployed**.

---

## 2. What actually happened

### 2.1 The first source hung silently for two minutes

```
12:35:24.639  resolved via indexer redirect to a magnet
12:35:25.102  fetching metadata for "1F97CD5A…" before adding the torrent
              (nothing — ever again)
12:37:19.761  resolved via direct magnet link          ← a different source
```

`1F97CD5A…` appears **exactly once** in the entire log. It never got metadata, never failed, never
timed out, and never logged another line. It sat in `Preparing` until you gave up and picked
something else, roughly two minutes later.

This is by design, and the design is written down: `StreamSession.State` carries the comment *"There
is deliberately no timeout on reaching Ready: a popular torrent can take minutes to find its first
reachable peer … The viewer decides when to give up, not a constant."* The reasoning is sound. The
consequence is that **the true wait was 6 m 21 s** (12:35:24 → 12:41:45), not the 4 m 25 s the second
session accounts for — which matches your "more than 5 mins" exactly.

### 2.2 The session that did play

`A13B09546A79DAEDA8A2CA55668FFDBD523B47DF` — `Minions.and.Monsters.2026.1080p.WEB.h264-ETHEL.mkv`,
4,903,563,260 bytes, **7,267,897 bps → 908,487 B/s required to sustain**.

```
12:37:19.764  fetching metadata
~12:37:51.46  torrent added        (inferred: forced announce fires 5s later, at 12:37:56.460)
12:38:06      t+10s  open=0   peers[Available=0,  Leechs=0, Seeds=0]
12:38:16      t+20s  open=2   peers[Available=39, Leechs=0, Seeds=2]
12:38:26      t+30s  open=5   peers[Available=27, Leechs=0, Seeds=5]
12:38:56      t+60s  open=8   peers[Available=0,  Leechs=2, Seeds=6]
12:41:15.372  ffprobe's first read at offset 0        ← prebuffer finished
12:41:31.733  read at offset 7,864,320 blocked 16,308ms        (head)
12:41:43.787  read at offset 4,902,514,684 blocked 12,020ms    (tail, 1 MB from EOF)
12:41:45.124  session ready
```

| Phase | Duration |
|---|---|
| Magnet → metadata | 31.7 s |
| Prebuffer (first **and last** piece) | **3 m 23.9 s** |
| ffprobe (12 MB head, then tail) | 29.8 s |
| **Total, press-play → Ready** | **4 m 25.4 s** |

### 2.3 Playback never reached the bitrate

| Tick | To player | Swarm | Seeds seen | Stalls |
|---|---|---|---|---|
| 12:42:22 | 620,672 B/s | 655,843 B/s | 31 | 4, worst 16,308 ms |
| 12:43:22 | 624,728 B/s | 686,380 B/s | 39 | 4, worst 12,368 ms |
| 12:44:22 | 873,879 B/s | 1,088,005 B/s | 52 | 5, worst 6,088 ms |
| 12:45:22 | 581,081 B/s | 1,182,240 B/s | 56 | 3, worst 6,752 ms |

Required: **908,487 B/s**. The best minute reached 873,879 — 96 % of it, and only briefly. 16 stalls
in total, the worst blocking 16.3 seconds.

The stalled reads land at exactly 8,388,608-byte intervals — 16,520,953 / 24,909,561 / 33,298,169 /
41,686,777 / 50,075,385 / 58,463,993 / 66,852,601 / 75,241,209 / 83,629,817 / 92,018,425. That is the
player requesting orderly sequential 8 MB chunks and each one waiting on pieces that had not arrived.
**The read path is behaving correctly.** It is being starved.

Progress at the last heartbeat: 3.4 % of a 4.9 GB file, eight minutes in.

---

## 3. Root cause

**The swarm ramp, not the swarm.** At t+20 s the trackers had already reported **39 seeds** and the
engine was connected to **2**. At t+60 s it was connected to **8** — exactly
`MaxHalfOpenConnections`. Seeds kept climbing (31 → 39 → 52 → 56) and the swarm rate was *still
rising* (655 → 1,182 KB/s) when playback stopped at ~8 minutes. That is the signature of a
connection ramp that never finished, not of a thin swarm.

Everything downstream inherits it. The prebuffer waits for the first **and last** piece; at
600 KB/s across ~5 connections, fetching a tail piece 4.9 GB into the file took most of those 3 m 24 s.
ffprobe then re-read that same tail and blocked another 12 s on it.

Contributing, and already documented: `dht=NotReady` throughout. MonoTorrent 3.0.2's DHT never
bootstraps here, so peer discovery is trackers-only — which is exactly why `TrackerList` exists.

---

## 4. Why the obvious fix is not the fix

Raising the connection budget is the direct lever, and the constants carry a long comment explaining
why they are where they are:

| Setting | Attempt rate | NAT entries | Outcome |
|---|---|---|---|
| 8 / 10 s | 0.8/s | ≈ 96 | current — the only setting observed to be safe |
| 20 / 10 s | 2.0/s | ≈ 240 | **tried 2026-08-13, LAN dropped, reverted** |
| 20 / 5 s | 4.0/s | ≈ 480 | the August config that took the whole house down |

The 2026-08-13 attempt is the important one: it was arithmetically about half the known-bad load and
it *still* coincided with the LAN dropping — an SSH session to the box was reset mid-test, not merely
the torrent poll. One observation is not proof, but the cost of being wrong is every device in the
house losing connectivity.

The code ends that comment with a directive, and it is the right one:

> do not raise this again on arithmetic alone. The lever that adds peers WITHOUT adding outbound NAT
> entries is inbound reachability (AllowPortForwarding); prove that one in isolation first.

For completeness, the server's own conntrack table is fine — **1,243 of 262,144** — so the Debian box
is not the constraint. The household router is, and it is unmeasurable from here.

---

## 5. The finding that changes the plan

**Inbound reachability has never been possible, so it has never actually been tried.**

```
LISTEN 0  6  0.0.0.0:46497  users:(("jellyfin",pid=244556,fd=642))
LISTEN 0  6     [::]:45319  users:(("jellyfin",pid=244556,fd=643))
```

MonoTorrent is listening on a **random ephemeral port**, reassigned on every restart.
`EngineSettingsBuilder` sets `AllowPortForwarding = true` but never a `ListenEndPoint`, so:

- every restart asks the router to map a *different* port;
- a manual, static port-forward cannot be configured at all;
- if UPnP is refused or disabled on the router, there is no fallback and no way to tell.

This explains the shape of the whole trace. `open=8` sitting exactly at the half-open cap means
essentially **every** connection was outbound, chased a few at a time. Behind NAT with no reachable
port, a client can only ever talk to peers that accept inbound — roughly a third of a swarm; the rest
can see us but can never reach us.

An inbound peer costs **zero** outbound NAT entries. It is the one lever that adds peers without
touching the budget that took the LAN down twice.

---

## 6. What I propose to do

### Step 1 — Pin the listen port (no household risk)

Add a fixed `ListenEndPoints` to `EngineSettingsBuilder` in `GetOrCreateEngine()`
(`TorrentStreamService.cs:~1310`), on a high port, with the number in `PluginConfiguration` so it is
changeable from the dashboard rather than hardcoded.

Why this is safe: it changes *which* port is listened on, not how many connections are attempted. The
outbound attempt rate — the thing implicated in both outages — is untouched.

What it enables: a stable UPnP mapping, and failing that, a manual forward you can set once in the
router. Both make the server reachable, which is the only source of free peers.

### Step 2 — Report inbound reachability instead of guessing at it

The engine currently cannot say whether port forwarding worked. Add to the existing heartbeat:
whether MonoTorrent's port-forwarding reports a successful mapping, and the listen port in use — so
one glance at the log answers "is the router cooperating".

### Step 3 — Re-run the same title and read `conns[N open of M avail, S seed]`

Three outcomes, each with a clear next move:

| Observation | Meaning | Next |
|---|---|---|
| open climbs well past 8 | inbound peers are arriving | done — measure the new time-to-first-frame |
| open still pinned at 8 | UPnP refused; forward the port manually | re-run after forwarding |
| open high but rate still < bitrate | genuinely a thin/slow swarm | rank harder on measured seeders |

### Step 4 — Only then, and only with numbers, revisit the connection budget

If inbound reachability lands and the ramp is still too slow, raising the budget becomes a decision
backed by measurement rather than arithmetic. Not before.

---

## 7. What I am deliberately not doing

- **Not raising `MaxHalfOpenConnections`.** Twice attempted, twice coincided with the whole house
  losing connectivity. It stays at 8 until inbound reachability has been proven in isolation, exactly
  as the code says.
- **Not moving ffprobe off the critical path.** It is worth ~30 s of a 265 s wait, and the comment at
  `TorrentStreamService.cs:1125` records why it was deliberately serialised: there is one `Stream`
  per session and a shared cursor, so probing alongside playback starves the play head. Reverting
  that reintroduces a bug already fixed.
- **Not dropping the last-piece prebuffer.** MKV cues and MP4 moov atoms live at the tail; without
  them a player opens the file but refuses to seek. Worth revisiting *after* the swarm is healthy,
  when it costs seconds rather than minutes.
- **Not touching the ranker.** The picked source had 56 seeds. It was not a bad pick.

---

## 8. Open question for you

The metadata-fetch hang in §2.1 cost two minutes with no feedback and no timeout. The current
behaviour is deliberate — the viewer decides when to give up. But `1F97CD5A…` produced *no* log line
after the first, and the app had nothing to show either.

Worth deciding separately: leave it, or have `/Sessions/{token}/status` distinguish "still finding
peers, 0 so far, 90 s elapsed" from "working on it", so the picker can offer another source before
two minutes pass. No code has been written for this either way.

---

## Appendix — evidence

- Log: `/var/log/jellyfin/jellyfin20260816.log` (root-readable only)
- Sessions: `1F97CD5AA1F6244F522EC9F5E0EBCD23110E3D01` (hung), `A13B09546A79DAEDA8A2CA55668FFDBD523B47DF` (played)
- 16 `blocked` warnings, 4 `playing` heartbeats, 0 `bytes short` aborts — no truncated deliveries, ever
- Trackers reachable from this host: `open.demonii.com:1337` OK, `tracker.publictracker.xyz:6969` OK,
  `zer0day.ch:1337` timed out on probe but answered `[Ok]` during the session,
  `tracker.opentrackr.org:1337` UDP timed out (its `http://` form answered)
- Engine build at time of writing: `dotnet build -c Release` → 0 warnings, 0 errors; 316/316 tests pass
