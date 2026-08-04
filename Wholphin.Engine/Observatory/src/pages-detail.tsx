import { useMemo, useState } from 'react';
import { usePoll, useEventStream, post, pick, type EngineEvent } from './api';
import {
  Section, Tiles, Tile, Table, Meter, Bars, Problem, Empty,
  fmtBytes, fmtMs, timing,
} from './ui';
import type { Snapshot } from './pages-core';

declare const ApiClient: { getUrl(path: string, params?: Record<string, unknown>): string };

export function Metadata({ snap }: { snap: Snapshot | undefined }) {
  const c = snap?.counters ?? {};
  const stats = usePoll<Record<string, unknown>>('OrcaEngine/Catalog/Stats', 60_000);
  const ops = ['enrich', 'discover', 'search', 'related', 'providers', 'videos', 'keywords', 'upcoming', 'genre'];

  return (
    <>
      <Section
        title="Provenance"
        subtitle="TMDB is the only external metadata provider wired into the engine today, alongside Jellyfin itself. Per-field provenance becomes meaningful once a second provider exists."
      >
        <Table head={['Field', 'Source']}>
          {[
            ['Poster', 'TMDb (external rows) · Jellyfin (library items)'],
            ['Backdrop', 'TMDb (external rows) · Jellyfin (library items)'],
            ['Overview', 'TMDb (external rows) · Jellyfin (library items)'],
            ['Genres / tags / people', 'TMDb (external rows) · Jellyfin (library items)'],
            ['Trailer URL', 'TMDb videos → YouTube'],
            ['Watch provider', 'TMDb'],
            ['Trivia / content advisories', 'LLM'],
          ].map(([field, source]) => (
            <tr key={field}><td>{field}</td><td className="obs-muted">{source}</td></tr>
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

  if (error) return <Problem error={error} />;

  return (
    <>
      <Section
        title="Resolver"
        subtitle="Trailers resolve through TMDB's videos endpoint, which names a YouTube video; yt-dlp then downloads it. TMDB is the only source."
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

      <Section title="States">
        {d && Object.keys(d.states).length > 0
          ? <Bars rows={Object.entries(d.states).map(([label, value]) => ({ label, value }))} />
          : <Empty>No trailer records yet.</Empty>}
      </Section>

      <Section
        title="Failures"
        subtitle="FailedPermanent means TMDB named no trailer at all. FailedTemporary means the download or transcode fell over — those retry."
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

const OPERATIONS: { label: string; path: string; params?: Record<string, unknown> }[] = [
  { label: 'Resync catalog', path: 'OrcaEngine/Catalog/Resync' },
  { label: 'Reconcile availability', path: 'OrcaEngine/Catalog/Reconcile' },
  { label: 'Enrich from TMDB', path: 'OrcaEngine/Catalog/EnrichTmdb', params: { maxItems: 100 } },
  { label: 'Pull global discovery', path: 'OrcaEngine/Discovery/PullGlobal' },
  { label: 'Recompute community ratings', path: 'OrcaEngine/Analytics/CommunityRating/Recompute' },
  { label: 'Purge unjustified rows', path: 'OrcaEngine/Catalog/PurgeUnjustified' },
];

export function Settings() {
  const [busy, setBusy] = useState<string>();
  const [message, setMessage] = useState<string>();

  const run = async (op: typeof OPERATIONS[number]) => {
    setBusy(op.label);
    setMessage(undefined);
    try {
      await post(op.path, op.params);
      setMessage(`${op.label}: done.`);
    } catch (e) {
      setMessage(`${op.label}: ${(e as Error).message}`);
    } finally {
      setBusy(undefined);
    }
  };

  const resetMetrics = async () => {
    if (!window.confirm('Reset all counters to zero? Timing averages and totals will be lost.')) return;
    setBusy('reset');
    try {
      await post('OrcaEngine/Admin/Metrics/Reset');
      setMessage('Counters reset.');
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setBusy(undefined);
    }
  };

  return (
    <>
      <Section
        title="Configuration"
        subtitle="Settings live on the plugin's own configuration page, which writes through Jellyfin's authenticated config API."
      >
        <a className="obs-btn obs-btn-primary" href="#/configurationpage?name=Orca%20Engine">Open Orca Engine settings</a>
      </Section>

      <Section title="Operations">
        <div className="obs-actions obs-wrap">
          {OPERATIONS.map((op) => (
            <button key={op.label} className="obs-btn" disabled={!!busy} onClick={() => run(op)}>
              {busy === op.label ? 'Working…' : op.label}
            </button>
          ))}
        </div>
      </Section>

      <Section title="Danger zone">
        <button className="obs-btn obs-btn-danger" disabled={!!busy} onClick={resetMetrics}>
          Reset all counters
        </button>
      </Section>

      {message && <p className="obs-note">{message}</p>}
      <p className="obs-muted obs-small">
        Diagnostics endpoint: <span className="obs-mono">{ApiClient.getUrl('OrcaEngine/Admin/Diagnostics')}</span>
      </p>
    </>
  );
}
