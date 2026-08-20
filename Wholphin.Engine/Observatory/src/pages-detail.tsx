import { useMemo, useState } from 'react';
import { usePoll, useEventStream, post, postFor, pick, type EngineEvent } from './api';
import {
  Section, Tiles, Tile, Table, Meter, Bars, Problem, Empty,
  fmtBytes, fmtMs, timing,
} from './ui';
import type { Snapshot } from './pages-core';

interface ProviderHealth {
  Name: string;
  Configured: boolean;
  Success: number;
  Empty: number;
  Failure: number;
  Timeout: number;
  RateLimited: number;
  ShortCircuited: number;
  ConsecutiveFailures: number;
  AvgLatencyMs: number;
  LastSuccessUtc: string | null;
  LastFailureUtc: string | null;
  LastFailureKind: string | null;
  BreakerOpen: boolean;
}

interface MetadataDiagnostics {
  Providers: ProviderHealth[];
  Priority: Record<string, string[]>;
  Coverage: { Total: number; NeverSynced: number; WithPoster: number; WithLogo: number; WithRatings: number } | null;
  Metrics: Record<string, number>;
}

/** A provider is only "off" if it has no key — that is a config choice, not a fault. */
function providerState(p: ProviderHealth): { label: string; tone: 'ok' | 'warn' | 'bad' } {
  if (p.BreakerOpen) return { label: 'Circuit open', tone: 'bad' };
  if (!p.Configured) return { label: 'No key', tone: 'warn' };
  if (p.ConsecutiveFailures > 0) return { label: 'Degraded', tone: 'warn' };
  return { label: 'Healthy', tone: 'ok' };
}

export function Metadata({ snap }: { snap: Snapshot | undefined }) {
  const c = snap?.counters ?? {};
  const stats = usePoll<Record<string, unknown>>('OrcaEngine/Catalog/Stats', 60_000);
  const diag = usePoll<MetadataDiagnostics>('OrcaEngine/Metadata/Diagnostics', 30_000);
  const ops = ['enrich', 'discover', 'search', 'related', 'providers', 'videos', 'keywords', 'upcoming', 'genre'];

  const providers = pick<ProviderHealth[]>(diag.data ?? {}, 'Providers') ?? [];
  const priority = pick<Record<string, string[]>>(diag.data ?? {}, 'Priority') ?? {};
  const coverage = pick<Record<string, number>>(diag.data ?? {}, 'Coverage') ?? {};
  const configured = providers.filter((p) => p.Configured).length;
  const broken = providers.filter((p) => p.BreakerOpen).length;

  return (
    <>
      <Section
        title="Providers"
        subtitle="Each provider declares which fields it can supply, and is asked only for the ones a title is actually missing. A miss is not a failure — it just means that provider had nothing for that title."
        actions={<button className="obs-btn" onClick={diag.reload}>Refresh</button>}
      >
        <Tiles>
          <Tile label="Configured" value={`${configured} of ${providers.length || '—'}`} tone={configured > 1 ? 'ok' : undefined} />
          <Tile label="Circuits open" value={broken} tone={broken > 0 ? 'bad' : 'ok'} hint="Providers temporarily withdrawn after repeated failures" />
          <Tile label="With ratings" value={`${coverage['WithRatings'] ?? 0}`} hint="Catalog rows carrying external critic scores" />
          <Tile label="With logo" value={`${coverage['WithLogo'] ?? 0}`} hint="Catalog rows carrying a clear logo" />
        </Tiles>

        <Table
          head={['Provider', 'State', 'OK', 'Empty', 'Errors', 'Timeouts', '429s', 'Avg', 'Last success']}
          empty={!providers.length}
        >
          {providers.map((p) => {
            const state = providerState(p);
            return (
              <tr key={p.Name}>
                <td className="obs-mono">{p.Name}</td>
                <td className={state.tone === 'ok' ? undefined : `obs-${state.tone}`}>{state.label}</td>
                <td>{p.Success}</td>
                <td className="obs-muted">{p.Empty}</td>
                <td className={p.Failure > 0 ? 'obs-bad' : undefined}>{p.Failure}</td>
                <td className={p.Timeout > 0 ? 'obs-warn' : undefined}>{p.Timeout}</td>
                <td className={p.RateLimited > 0 ? 'obs-warn' : undefined}>{p.RateLimited}</td>
                <td>{fmtMs(p.AvgLatencyMs)}</td>
                <td className="obs-muted">{p.LastSuccessUtc ? new Date(p.LastSuccessUtc).toLocaleString() : '—'}</td>
              </tr>
            );
          })}
        </Table>
        {providers.some((p) => p.LastFailureKind) && (
          <p className="obs-muted obs-small">
            Last failures: {providers.filter((p) => p.LastFailureKind).map((p) => `${p.Name} → ${p.LastFailureKind}`).join(' · ')}
          </p>
        )}
      </Section>

      <Section
        title="Field priority"
        subtitle="Which provider wins each field, in order. Artwork is additionally scored on language and resolution, so a higher-priority provider does not win with a worse image."
      >
        <Table head={['Field group', 'Order']} empty={!Object.keys(priority).length}>
          {Object.entries(priority).map(([field, order]) => (
            <tr key={field}>
              <td>{field}</td>
              <td className="obs-mono obs-muted">{(order ?? []).join(' → ') || '—'}</td>
            </tr>
          ))}
        </Table>
      </Section>

      <Section title="TMDB calls">
        <Table head={['Operation', 'Calls', 'OK', 'Errors']} empty={ops.every((o) => !c[`tmdb.${o}.ok`] && !c[`tmdb.${o}.error`])}>
          {ops.map((op) => {
            const ok = c[`tmdb.${op}.ok`] ?? 0;
            const err = c[`tmdb.${op}.error`] ?? 0;
            if (!ok && !err) return null;
            return (
              <tr key={op}>
                <td className="obs-mono">{op}</td>
                <td>{ok + err}</td>
                <td>{ok}</td>
                <td className={err > 0 ? 'obs-bad' : undefined}>{err}</td>
              </tr>
            );
          })}
        </Table>
      </Section>

      {stats.error && <Problem error={stats.error} />}
      {stats.data && (
        <Section title="Catalog">
          <pre className="obs-json">{JSON.stringify(stats.data, null, 2)}</pre>
        </Section>
      )}
    </>
  );
}

interface TrailerDiagnostics {
  Available: boolean;
  QueueDepth: number;
  States: Record<string, number>;
  Cache: { ReadyCount: number; TotalBytes: number; PinnedCount: number };
  Failures: {
    TmdbId: number; MediaType: number | string; State: string;
    FailureReason: string | null; FailureCount: number; UpdatedAt: string;
  }[];
  Metrics: Record<string, number>;
}

export function TrailerResolver() {
  const { data, error, reload } = usePoll<TrailerDiagnostics>('OrcaEngine/Trailer/Diagnostics', 15_000);
  const d = data && {
    available: pick<boolean>(data, 'Available'),
    queueDepth: pick<number>(data, 'QueueDepth') ?? 0,
    states: pick<Record<string, number>>(data, 'States') ?? {},
    cache: pick<Record<string, number>>(data, 'Cache') ?? {},
    failures: pick<TrailerDiagnostics['Failures']>(data, 'Failures') ?? [],
  };

  // Per-source counters, discovered from the metric keys so a source added later needs no edit here.
  const metrics = (pick<Record<string, number>>(data ?? {}, 'Metrics') ?? {});
  const sources = [...new Set(
    Object.keys(metrics)
      .filter((k) => k.startsWith('trailer.resolve.') && (k.endsWith('.ok') || k.endsWith('.miss')))
      .map((k) => k.split('.')[2]),
  )].sort().map((name) => ({
    name,
    ok: metrics[`trailer.resolve.${name}.ok`] ?? 0,
    miss: metrics[`trailer.resolve.${name}.miss`] ?? 0,
  }));

  if (error) return <Problem error={error} />;

  return (
    <>
      <Section
        title="Resolver"
        subtitle="Sources are tried in the configured order until one names a video; yt-dlp then downloads it. The last source searches YouTube directly and scores the results, so a title no provider has a video for can still get one."
        actions={<button className="obs-btn" onClick={reload}>Refresh</button>}
      >
        <Tiles>
          <Tile
            label="Binaries"
            value={d?.available ? 'Ready' : 'Missing'}
            tone={d?.available ? 'ok' : 'bad'}
            hint="yt-dlp + ffmpeg"
          />
          <Tile label="Queue depth" value={d?.queueDepth ?? 0} />
          <Tile label="Cached trailers" value={String(d?.cache['ReadyCount'] ?? d?.cache['readyCount'] ?? 0)} />
          <Tile label="Cache size" value={fmtBytes(Number(d?.cache['TotalBytes'] ?? d?.cache['totalBytes'] ?? 0))} />
        </Tiles>
        <Meter value={d?.queueDepth ?? 0} max={500} caption={`${d?.queueDepth ?? 0} of 500 queued`} />
      </Section>

      <Section
        title="Where trailers came from"
        subtitle="Sources are tried in the configured order until one answers. A miss is not a failure — it just means the next source was asked."
      >
        <Table head={['Source', 'Resolved', 'Missed', 'Hit rate']} empty={!sources.length}>
          {sources.map((s) => (
            <tr key={s.name}>
              <td className="obs-mono">{s.name}</td>
              <td>{s.ok}</td>
              <td className="obs-muted">{s.miss}</td>
              <td className={s.ok === 0 && s.miss > 0 ? 'obs-warn' : undefined}>
                {s.ok + s.miss > 0 ? `${((s.ok / (s.ok + s.miss)) * 100).toFixed(0)}%` : '—'}
              </td>
            </tr>
          ))}
        </Table>
      </Section>

      <Section title="States">
        {d && Object.keys(d.states).length > 0
          ? <Bars rows={Object.entries(d.states).map(([label, value]) => ({ label, value }))} />
          : <Empty>No trailer records yet.</Empty>}
      </Section>

      <Section
        title="Failures"
        subtitle="FailedPermanent means every configured source came back empty — including the scored YouTube search, which declines rather than guess. FailedTemporary means the download or transcode fell over; those retry."
      >
        <Table head={['TMDB id', 'Type', 'State', 'Reason', 'Attempts', 'Last tried']} empty={!d?.failures.length}>
          {d?.failures.map((f, i) => (
            <tr key={`${f.TmdbId ?? pick(f, 'tmdbId')}-${i}`}>
              <td className="obs-mono">{String(pick(f, 'TmdbId'))}</td>
              <td>{String(pick(f, 'MediaType'))}</td>
              <td className={String(pick(f, 'State')).includes('Permanent') ? 'obs-muted' : 'obs-warn'}>
                {String(pick(f, 'State')).replace('Failed', '')}
              </td>
              <td className="obs-muted">{String(pick(f, 'FailureReason') ?? '—')}</td>
              <td>{String(pick(f, 'FailureCount') ?? 0)}</td>
              <td className="obs-muted">{new Date(String(pick(f, 'UpdatedAt'))).toLocaleString()}</td>
            </tr>
          ))}
        </Table>
      </Section>
    </>
  );
}

export function Recommendations() {
  const runs = usePoll<Record<string, unknown>[]>('OrcaEngine/Discovery/Runs', 60_000, { limit: 20 });
  const sources = usePoll<Record<string, unknown>[]>('OrcaEngine/Discovery/SourceStats', 60_000);
  const picks = usePoll<Record<string, unknown>[]>('OrcaEngine/Discovery/Picks', 0);
  const [open, setOpen] = useState<number | null>(null);

  const latest = runs.data?.[0];
  const funnel = latest && [
    { label: 'Generated', value: Number(pick(latest, 'Generated') ?? 0) },
    { label: 'Filtered out', value: Number(pick(latest, 'FilteredOut') ?? 0) },
    { label: 'Scored', value: Number(pick(latest, 'Scored') ?? 0) },
    { label: 'Below threshold', value: Number(pick(latest, 'BelowThreshold') ?? 0) },
    { label: 'Selected', value: Number(pick(latest, 'Selected') ?? 0) },
    { label: 'Imported', value: Number(pick(latest, 'Imported') ?? 0) },
  ];

  return (
    <>
      {runs.error && <Problem error={runs.error} />}

      <Section title="Latest discovery run" subtitle={latest ? `${fmtMs(Number(pick(latest, 'DurationMs')))} · ${new Date(String(pick(latest, 'StartedAt'))).toLocaleString()}` : undefined}>
        {funnel ? <Bars rows={funnel} /> : <Empty>No discovery runs recorded yet.</Empty>}
        {latest && pick<string>(latest, 'FilterReasonsJson') && (
          <>
            <h3 className="obs-h3">Why candidates were dropped</h3>
            <pre className="obs-json">{prettyJson(String(pick(latest, 'FilterReasonsJson')))}</pre>
          </>
        )}
      </Section>

      <Section title="Sources">
        <Table head={['Source', 'Picks', 'Engaged', 'Avg score']} empty={!sources.data?.length}>
          {sources.data?.map((s, i) => (
            <tr key={i}>
              <td className="obs-mono">{String(pick(s, 'Source') ?? pick(s, 'SourceType') ?? '—')}</td>
              <td>{String(pick(s, 'Picks') ?? 0)}</td>
              <td>{String(pick(s, 'Engaged') ?? 0)}</td>
              <td>{Number(pick(s, 'AvgFinalScore') ?? 0).toFixed(3)}</td>
            </tr>
          ))}
        </Table>
      </Section>

      <Section
        title="Why was this recommended?"
        subtitle="Every external recommendation carries its full justification. Click a row for the score breakdown and source attribution."
      >
        <Table head={['Title', 'Reason', 'Source', 'Final', 'Taste', 'Popularity', 'Freshness']} empty={!picks.data?.length}>
          {picks.data?.slice(0, 100).flatMap((p, i) => {
            const id = Number(pick(p, 'Id') ?? i);
            const rows = [
              <tr key={id} className="obs-clickable" onClick={() => setOpen(open === id ? null : id)}>
                <td>{String(pick(p, 'Title') ?? '—')}</td>
                <td className="obs-muted">{String(pick(p, 'Reason') ?? '—')}</td>
                <td className="obs-mono obs-small">{String(pick(p, 'SourceType') ?? '—')}</td>
                <td>{Number(pick(p, 'FinalScore') ?? 0).toFixed(3)}</td>
                <td>{Number(pick(p, 'TasteScore') ?? 0).toFixed(3)}</td>
                <td>{Number(pick(p, 'PopularityScore') ?? 0).toFixed(3)}</td>
                <td>{Number(pick(p, 'FreshnessScore') ?? 0).toFixed(3)}</td>
              </tr>,
            ];
            if (open === id) {
              rows.push(
                <tr key={`${id}-detail`}>
                  <td colSpan={7}>
                    <pre className="obs-json">{prettyJson(String(pick(p, 'ScoreExplanationJson') ?? '{}'))}</pre>
                  </td>
                </tr>,
              );
            }
            return rows;
          })}
        </Table>
      </Section>
    </>
  );
}

function prettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

const LEVELS = ['info', 'warn', 'error'] as const;

export function LiveLogs() {
  const [paused, setPaused] = useState(false);
  const [levels, setLevels] = useState<Set<string>>(new Set(LEVELS));
  const [component, setComponent] = useState('');
  const [search, setSearch] = useState('');
  const [expanded, setExpanded] = useState<Set<number>>(new Set());
  const { events, connected, clear } = useEventStream(paused);

  const components = useMemo(
    () => [...new Set(events.map((e) => e.component))].sort(),
    [events],
  );

  const filtered = useMemo(() => {
    const needle = search.toLowerCase();
    return events
      .filter((e) => levels.has(e.level))
      .filter((e) => !component || e.component === component)
      .filter((e) => !needle
        || e.event.toLowerCase().includes(needle)
        || e.component.toLowerCase().includes(needle)
        || (e.data ?? '').toLowerCase().includes(needle)
        || (e.exception ?? '').toLowerCase().includes(needle))
      .slice()
      .reverse();
  }, [events, levels, component, search]);

  const toggleLevel = (level: string) => setLevels((prev) => {
    const next = new Set(prev);
    if (next.has(level)) next.delete(level); else next.add(level);
    return next;
  });

  const exportLogs = () => {
    const blob = new Blob([JSON.stringify(filtered, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `orca-events-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <Section
      title="Live logs"
      subtitle={`${filtered.length} of ${events.length} events${connected ? '' : ' · reconnecting…'}`}
      actions={
        <div className="obs-actions">
          <button className="obs-btn" onClick={() => setPaused((p) => !p)}>{paused ? 'Resume' : 'Pause'}</button>
          <button className="obs-btn" onClick={exportLogs} disabled={!filtered.length}>Export</button>
          <button className="obs-btn" onClick={clear}>Clear</button>
        </div>
      }
    >
      <div className="obs-filters">
        <span className={`obs-dot ${connected && !paused ? 'obs-ok-bg' : 'obs-off-bg'}`} aria-hidden="true" />
        {LEVELS.map((level) => (
          <label key={level} className="obs-check">
            <input type="checkbox" checked={levels.has(level)} onChange={() => toggleLevel(level)} />
            {level}
          </label>
        ))}
        <select value={component} onChange={(e) => setComponent(e.target.value)} className="obs-select">
          <option value="">All components</option>
          {components.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
        <input
          className="obs-input"
          placeholder="Search events, detail, stack traces…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </div>

      <div className="obs-log">
        {filtered.length === 0 && <Empty>Nothing matches. Events appear as the engine works.</Empty>}
        {filtered.map((e) => (
          <LogRow
            key={e.seq}
            event={e}
            expanded={expanded.has(e.seq)}
            onToggle={() => setExpanded((prev) => {
              const next = new Set(prev);
              if (next.has(e.seq)) next.delete(e.seq); else next.add(e.seq);
              return next;
            })}
          />
        ))}
      </div>
    </Section>
  );
}

function LogRow({ event, expanded, onToggle }: { event: EngineEvent; expanded: boolean; onToggle: () => void }) {
  const time = new Date(event.at).toLocaleTimeString();
  return (
    <div className={`obs-logrow obs-level-${event.level}`}>
      <button className="obs-logline" onClick={onToggle} disabled={!event.exception}>
        <span className="obs-logtime">{time}</span>
        <span className={`obs-loglevel obs-level-${event.level}`}>{event.level}</span>
        <span className="obs-logcomp">{event.component}</span>
        <span className="obs-logevent">{event.event}</span>
        {event.elapsedMs !== null && <span className="obs-logms">{fmtMs(event.elapsedMs)}</span>}
        {event.data && <span className="obs-logdata">{event.data}</span>}
        {event.exception && <span className="obs-logmore">{expanded ? '▾' : '▸'}</span>}
      </button>
      {expanded && event.exception && <pre className="obs-trace">{event.exception}</pre>}
    </div>
  );
}

export function Timeline() {
  const { events } = useEventStream(false);
  const recent = events.slice(-200);

  const lanes = useMemo(() => {
    const byComponent = new Map<string, EngineEvent[]>();
    for (const e of recent) {
      const list = byComponent.get(e.component) ?? [];
      list.push(e);
      byComponent.set(e.component, list);
    }
    return [...byComponent.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [recent]);

  if (recent.length === 0) return <Empty>Waiting for the engine to do something.</Empty>;

  const start = new Date(recent[0].at).getTime();
  const end = Math.max(new Date(recent[recent.length - 1].at).getTime(), start + 1000);
  const span = end - start;

  return (
    <Section
      title="Engine timeline"
      subtitle="What the engine has actually been doing, one lane per subsystem. Width is duration where it was measured."
    >
      <div className="obs-timeline">
        {lanes.map(([component, items]) => (
          <div key={component} className="obs-lane">
            <span className="obs-lane-label">{component}</span>
            <div className="obs-lane-track">
              {items.map((e) => {
                const left = ((new Date(e.at).getTime() - start) / span) * 100;
                const width = Math.max(0.6, ((e.elapsedMs ?? 200) / span) * 100);
                return (
                  <span
                    key={e.seq}
                    className={`obs-lane-mark obs-level-${e.level}`}
                    style={{ left: `${left}%`, width: `${Math.min(width, 100 - left)}%` }}
                    title={`${new Date(e.at).toLocaleTimeString()} · ${e.event}${e.elapsedMs !== null ? ` · ${fmtMs(e.elapsedMs)}` : ''}`}
                  />
                );
              })}
            </div>
          </div>
        ))}
      </div>
      <div className="obs-timeline-axis">
        <span>{new Date(start).toLocaleTimeString()}</span>
        <span>{new Date(end).toLocaleTimeString()}</span>
      </div>
    </Section>
  );
}

export function Users({ snap }: { snap: Snapshot | undefined }) {
  const status = usePoll<Record<string, unknown>>('OrcaEngine/Admin/Status', 60_000);
  const behavior = usePoll<Record<string, unknown>>('OrcaEngine/Behavior/Stats', 60_000);
  const recompute = timing(snap?.counters, 'personalization.recompute');

  return (
    <>
      {status.error && <Problem error={status.error} />}
      <HistoryImport />
      <Section title="Audience">
        <Tiles>
          <Tile label="Profiles" value={String(pick(status.data, 'Profiles') ?? 0)} />
          <Tile label="Behavior events" value={String(pick(status.data, 'BehaviorEvents') ?? 0)} />
          <Tile label="Roaming settings" value={String(pick(status.data, 'RoamingSettings') ?? 0)} />
          <Tile label="Recompute queue" value={snap?.engine.recomputeQueueDepth ?? 0} />
        </Tiles>
      </Section>

      <Section title="Profile recomputation">
        <Tiles>
          <Tile label="Recomputes" value={recompute.count} />
          <Tile label="Average" value={fmtMs(recompute.avgMs)} />
        </Tiles>
      </Section>

      {behavior.data && (
        <Section title="Behavior signals">
          <pre className="obs-json">{JSON.stringify(behavior.data, null, 2)}</pre>
        </Section>
      )}
    </>
  );
}

/** One user's row in the import run. Mirrors the server's HistoryImportUser. */
interface ImportUser {
  userId: string;
  userName: string;
  state: string;
  itemsScanned: number;
  itemsTotal: number;
  eventsImported: number;
  unresolved: number;
  confidence: number;
  error: string | null;
}

interface ImportProgress {
  running: boolean;
  phase: string;
  startedUtc: string | null;
  finishedUtc: string | null;
  usersTotal: number;
  usersDone: number;
  eventsImported: number;
  error: string | null;
  users: ImportUser[];
}

function toImport(raw: unknown): ImportProgress | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  const num = (source: unknown, key: string) => Number(pick(source, key) ?? 0);
  const str = (source: unknown, key: string) => (pick<string>(source, key) ?? null) as string | null;

  return {
    running: !!pick(raw, 'Running'),
    phase: String(pick(raw, 'Phase') ?? 'idle'),
    startedUtc: str(raw, 'StartedUtc'),
    finishedUtc: str(raw, 'FinishedUtc'),
    usersTotal: num(raw, 'UsersTotal'),
    usersDone: num(raw, 'UsersDone'),
    eventsImported: num(raw, 'EventsImported'),
    error: str(raw, 'Error'),
    users: (pick<unknown[]>(raw, 'Users') ?? []).map((u) => ({
      userId: String(pick(u, 'UserId') ?? ''),
      userName: String(pick(u, 'UserName') ?? '?'),
      state: String(pick(u, 'State') ?? 'pending'),
      itemsScanned: num(u, 'ItemsScanned'),
      itemsTotal: num(u, 'ItemsTotal'),
      eventsImported: num(u, 'EventsImported'),
      unresolved: num(u, 'Unresolved'),
      confidence: num(u, 'Confidence'),
      error: str(u, 'Error'),
    })),
  };
}

/** Mirrors WatchHistoryImporter.TargetConfidence — what a full history import should reach. */
const TARGET_CONFIDENCE = 0.8;

const n = (value: number) => value.toLocaleString();

/**
 * The one-time backfill of Jellyfin's existing watch history, and its per-user progress.
 *
 * Polls at 2s whether or not a run is in flight: the endpoint is an in-memory read, and starting a
 * timer only while running cannot show a run that another browser tab began, or one left going
 * while this page was closed.
 */
function HistoryImport() {
  const { data, error, reload } = usePoll<unknown>('OrcaEngine/Behavior/ImportHistory', 2000);
  const progress = useMemo(() => toImport(data), [data]);
  const [starting, setStarting] = useState(false);
  const [problem, setProblem] = useState<string>();

  const running = progress?.running ?? false;

  const start = async () => {
    setStarting(true);
    setProblem(undefined);
    try {
      await post('OrcaEngine/Behavior/ImportHistory');
      reload();
    } catch (e) {
      setProblem((e as Error).message);
    } finally {
      setStarting(false);
    }
  };

  return (
    <Section
      title="Watch history import"
      subtitle={
        'Live capture only ever sees what happens after the plugin was installed. This reads every '
        + 'user\u2019s existing Jellyfin history \u2014 played, play count, favourite, rating \u2014 and '
        + 'backfills the taste profiles from it. Run once at setup; safe to re-run.'
      }
      actions={
        <button className="obs-btn obs-btn-primary" onClick={start} disabled={running || starting}>
          {running ? 'Importing\u2026' : 'Import all watch history'}
        </button>
      }
    >
      {(problem ?? error) && <Problem error={(problem ?? error) as string} />}
      {progress?.error && <Problem error={progress.error} />}

      <Tiles>
        <Tile label="Status" value={progress?.phase ?? 'idle'} tone={running ? 'warn' : undefined} />
        <Tile label="Users" value={`${progress?.usersDone ?? 0} / ${progress?.usersTotal ?? 0}`} />
        <Tile label="Events imported" value={n(progress?.eventsImported ?? 0)} />
      </Tiles>

      <Table
        head={['User', 'Progress', 'Events', 'Unresolved', 'Confidence']}
        empty={!progress?.users.length}
      >
        {progress?.users.map((u) => (
          <tr key={u.userId}>
            <td>
              {u.userName}
              <div className="obs-muted obs-small">{u.error ?? u.state}</div>
            </td>
            <td className="obs-progress-cell">
              <Meter
                value={u.state === 'done' ? u.itemsTotal : u.itemsScanned}
                max={Math.max(1, u.itemsTotal)}
                tone={u.state === 'failed' ? 'bad' : u.state === 'done' ? 'ok' : 'warn'}
                caption={
                  u.state === 'done'
                    ? `${n(u.itemsTotal)} items scanned`
                    : `${n(u.itemsScanned)} / ${n(u.itemsTotal)}`
                }
              />
            </td>
            <td>{n(u.eventsImported)}</td>
            <td className={u.unresolved > 0 ? 'obs-warn' : undefined}>{n(u.unresolved)}</td>
            <td className={u.confidence >= TARGET_CONFIDENCE ? 'obs-ok' : 'obs-bad'}>
              {u.state === 'done' ? `${Math.round(u.confidence * 100)}%` : '\u2014'}
            </td>
          </tr>
        ))}
      </Table>
    </Section>
  );
}

interface ProbeResult {
  provider: string;
  ok: boolean;
  outcome: string;
  elapsedMs: number | null;
  kind: string | null;
  dimensions: number;
  relatedScore: number;
  unrelatedScore: number;
  message: string;
}

function toProbe(raw: unknown): ProbeResult {
  return {
    provider: String(pick(raw, 'Provider') ?? '?'),
    ok: !!pick(raw, 'Ok'),
    outcome: String(pick(raw, 'Outcome') ?? 'unknown'),
    elapsedMs: pick(raw, 'ElapsedMs') == null ? null : Number(pick(raw, 'ElapsedMs')),
    kind: (pick<string>(raw, 'Kind') ?? null) as string | null,
    dimensions: Number(pick(raw, 'Dimensions') ?? 0),
    relatedScore: Number(pick(raw, 'RelatedScore') ?? 0),
    unrelatedScore: Number(pick(raw, 'UnrelatedScore') ?? 0),
    message: String(pick(raw, 'Message') ?? ''),
  };
}

/**
 * Embedding health.
 *
 * Everything else the dashboard knows about embeddings is a negative signal: an alert appears when
 * something breaks, so a quiet page means "nothing failed since startup" — which includes "nothing
 * was tried". The Test button is the positive check, and it calls the provider directly rather than
 * through the fallback, so a dead provider cannot answer for a working one.
 */
export function Embeddings() {
  const { data, error, reload } = usePoll<Record<string, unknown>>('OrcaEngine/Embedding/Diagnostics', 15_000);
  const [probe, setProbe] = useState<ProbeResult>();
  const [testing, setTesting] = useState(false);
  const [problem, setProblem] = useState<string>();

  const configured = String(pick(data, 'ConfiguredProvider') ?? '—');
  const active = String(pick(data, 'ActiveProvider') ?? '—');
  const usingFallback = !!pick(data, 'UsingFallback');
  const batch = pick<Record<string, unknown>>(data, 'BatchSize') ?? {};
  const index = pick<Record<string, unknown>>(data, 'Index') ?? null;
  const providers = pick<Record<string, unknown>[]>(data, 'Providers') ?? [];
  const metrics = pick<Record<string, number>>(data, 'Metrics') ?? {};
  const staleBuilds = metrics['embedding.index.served_stale'] ?? 0;
  const stored = Number(pick(data, 'StoredVectors') ?? 0);

  const runTest = async () => {
    setTesting(true);
    setProblem(undefined);
    try {
      setProbe(toProbe(await postFor<unknown>('OrcaEngine/Embedding/Test')));
      reload();
    } catch (e) {
      setProblem((e as Error).message);
    } finally {
      setTesting(false);
    }
  };

  return (
    <>
      {(problem ?? error) && <Problem error={(problem ?? error) as string} />}

      <Section
        title="Provider"
        subtitle="Which model is actually producing your content vectors — not which one is selected."
        actions={
          <button className="obs-btn obs-btn-primary" onClick={runTest} disabled={testing}>
            {testing ? 'Testing\u2026' : 'Test provider'}
          </button>
        }
      >
        <Tiles>
          <Tile label="Configured" value={configured} hint={String(pick(data, 'ActiveModel') ?? '')} />
          <Tile
            label="In use"
            value={active}
            tone={usingFallback ? 'bad' : 'ok'}
            hint={usingFallback ? `'${configured}' could not be resolved` : 'Matches your selection'}
          />
          <Tile label="Batch size" value={String(pick(batch, 'Configured') ?? '—')} hint={`Allowed ${pick(batch, 'Min')}\u2013${pick(batch, 'Max')}`} />
          <Tile label="Retries per batch" value={String(pick(data, 'RetryAttemptsPerBatch') ?? '—')} />
        </Tiles>

        {probe && (
          <div className={`obs-probe obs-probe-${probe.ok && probe.outcome === 'healthy' ? 'ok' : probe.ok ? 'warn' : 'bad'}`}>
            <div className="obs-probe-head">
              <strong>{probe.provider}</strong>
              <span className="obs-probe-verdict">{probe.ok ? probe.outcome : 'failed'}</span>
            </div>
            <p>{probe.message}</p>
            {probe.ok && (
              <Tiles>
                <Tile label="Latency" value={fmtMs(probe.elapsedMs)} />
                <Tile label="Vector kind" value={probe.kind ?? '—'} />
                <Tile label="Dimensions" value={probe.dimensions} />
                <Tile
                  label="Related vs unrelated"
                  value={`${probe.relatedScore.toFixed(3)} / ${probe.unrelatedScore.toFixed(3)}`}
                  tone={probe.relatedScore > probe.unrelatedScore ? 'ok' : 'bad'}
                  hint="Two near-identical texts should score above an unrelated one"
                />
              </Tiles>
            )}
          </div>
        )}
      </Section>

      <Section
        title="Vector index"
        subtitle="Vectors are saved to the database, so a restart reuses them instead of re-embedding the catalog. Only new or edited titles cost a call."
      >
        {index === null ? (
          <Empty>No index is loaded yet. It is assembled the first time a recommendation surface needs it — from stored vectors where they exist.</Empty>
        ) : (
          <Tiles>
            <Tile label="Items indexed" value={String(pick(index, 'Count') ?? 0)} />
            <Tile label="Built by" value={String(pick(index, 'ProviderName') ?? '—')} tone="ok" />
            <Tile
              label="Built"
              value={pick(index, 'BuiltAtUtc') ? new Date(String(pick(index, 'BuiltAtUtc'))).toLocaleString() : '—'}
            />
            <Tile
              label="Stale serves"
              value={staleBuilds.toLocaleString()}
              tone={staleBuilds > 0 ? 'bad' : undefined}
              hint={staleBuilds > 0 ? 'A rebuild failed; this index is older than the catalog' : 'Every rebuild has succeeded'}
            />
          </Tiles>
        )}

        <Tiles>
          <Tile
            label="Saved to disk"
            value={stored.toLocaleString()}
            tone={stored > 0 ? 'ok' : undefined}
            hint={stored > 0 ? 'Survives a restart' : 'Nothing stored yet'}
          />
          <Tile label="Reused from disk" value={(metrics['embedding.index.reused'] ?? 0).toLocaleString()} />
          <Tile label="Newly embedded" value={(metrics['embedding.index.persisted'] ?? 0).toLocaleString()} />
        </Tiles>
      </Section>

      <Section
        title="Registered providers"
        subtitle="No provider stands in for another: two models produce vectors of different dimensions, so a failed rebuild keeps the old index rather than mixing them."
      >
        <Table head={['Provider', 'Configured', 'Model', 'Provider max', 'Effective batch', 'State']} empty={!providers.length}>
          {providers.map((p) => {
            const name = String(pick(p, 'Name') ?? '?');
            return (
              <tr key={name}>
                <td className="obs-mono">{name}</td>
                <td className={pick(p, 'IsConfigured') ? undefined : 'obs-muted'}>
                  {pick(p, 'IsConfigured') ? 'Yes' : 'No'}
                </td>
                <td className="obs-mono obs-muted">{String(pick(p, 'ModelId') || '—')}</td>
                <td>{String(pick(p, 'MaxBatchSize'))}</td>
                <td>{String(pick(p, 'EffectiveBatchSize'))}</td>
                <td className={pick(p, 'IsActive') ? 'obs-ok' : undefined}>
                  {pick(p, 'IsActive') ? 'In use' : pick(p, 'IsConfiguredChoice') ? 'Selected, unusable' : '—'}
                </td>
              </tr>
            );
          })}
        </Table>
      </Section>

      <Section title="Counters">
        <Table head={['Counter', 'Value']} empty={!Object.keys(metrics).length}>
          {Object.entries(metrics).map(([key, value]) => (
            <tr key={key}>
              <td className="obs-mono">{key}</td>
              <td>{value.toLocaleString()}</td>
            </tr>
          ))}
        </Table>
      </Section>
    </>
  );
}
