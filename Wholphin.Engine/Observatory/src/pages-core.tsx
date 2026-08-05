import { usePoll, rate, series, pick, type Sample } from './api';
import {
  Section, Tiles, Tile, Badge, Table, Meter, Spark, Bars, Problem,
  fmtBytes, fmtMs, fmtUptime, timing, discover,
} from './ui';

export interface Snapshot {
  plugin: string;
  version: string;
  schemaVersion: number;
  uptimeMinutes: number;
  process: { workingSetBytes: number; gcHeapBytes: number; processorTimeMs: number; scope: string };
  engine: {
    databaseBytes: number;
    cacheEntryLimit: number;
    trailerQueueDepth: number;
    recomputeQueueDepth: number;
  };
  integrations: { tmdbConfigured: boolean; arrConfigured: boolean; jellyseerrConfigured: boolean };
  counters: Record<string, number>;
  lastSeq: number;
  /** Sources the server could not read this tick, each naming itself and why. */
  problems: string[];
}

/**
 * Maps the raw snapshot into a fully-populated Snapshot.
 *
 * Jellyfin serializes property names as written, so the engine's responses are PascalCase — but
 * which casing a given endpoint yields depends on content negotiation, and the client should not
 * have to be sure. Every field is read case-insensitively and every nested object is guaranteed to
 * exist, so no page can crash on a missing branch. That mattered: `snap.process.scope` on a
 * PascalCase payload was one undefined lookup, and it took the whole dashboard down to a blank page.
 */
export function toSnapshot(raw: unknown): Snapshot | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  const process = pick<Record<string, unknown>>(raw, 'Process') ?? {};
  const engine = pick<Record<string, unknown>>(raw, 'Engine') ?? {};
  const integrations = pick<Record<string, unknown>>(raw, 'Integrations') ?? {};
  const num = (source: unknown, key: string) => Number(pick(source, key) ?? 0);

  return {
    plugin: String(pick(raw, 'Plugin') ?? 'Orca Engine'),
    version: String(pick(raw, 'Version') ?? ''),
    schemaVersion: num(raw, 'SchemaVersion'),
    uptimeMinutes: num(raw, 'UptimeMinutes'),
    process: {
      workingSetBytes: num(process, 'WorkingSetBytes'),
      gcHeapBytes: num(process, 'GcHeapBytes'),
      processorTimeMs: num(process, 'ProcessorTimeMs'),
      scope: String(pick(process, 'Scope') ?? ''),
    },
    engine: {
      databaseBytes: num(engine, 'DatabaseBytes'),
      cacheEntryLimit: num(engine, 'CacheEntryLimit'),
      trailerQueueDepth: num(engine, 'TrailerQueueDepth'),
      recomputeQueueDepth: num(engine, 'RecomputeQueueDepth'),
    },
    integrations: {
      tmdbConfigured: !!pick(integrations, 'TmdbConfigured'),
      arrConfigured: !!pick(integrations, 'ArrConfigured'),
      jellyseerrConfigured: !!pick(integrations, 'JellyseerrConfigured'),
    },
    // Metric keys are data, not property names — never re-cased.
    counters: (pick<Record<string, number>>(raw, 'Counters') ?? {}),
    lastSeq: num(raw, 'LastSeq'),
    problems: (pick<string[]>(raw, 'Problems') ?? []),
  };
}

/** CPU percentage from two processor-time samples. Whole process — see the Scope caption. */
function cpuPercent(samples: Sample[], snapshots: Snapshot[]): number | undefined {
  if (snapshots.length < 2 || samples.length < 2) return undefined;
  const wallMs = samples[samples.length - 1].at - samples[0].at;
  const cpuMs = snapshots[snapshots.length - 1].process.processorTimeMs - snapshots[0].process.processorTimeMs;
  if (wallMs <= 0 || cpuMs < 0) return undefined;
  return (cpuMs / wallMs / (navigator.hardwareConcurrency || 1)) * 100;
}

export function Overview({ snap, samples, snapshots }: {
  snap: Snapshot | undefined;
  samples: Sample[];
  snapshots: Snapshot[];
}) {
  const status = usePoll<Record<string, unknown>>('OrcaEngine/Admin/Status', 60_000);
  const cpu = cpuPercent(samples, snapshots);
  const c = snap?.counters;

  const homeBuilds = rate(samples, 'home.built');
  const cacheHits = rate(samples, 'home.cache_hit');
  const hitRate = homeBuilds + cacheHits > 0 ? (cacheHits / (homeBuilds + cacheHits)) * 100 : undefined;
  const build = timing(c, 'home.build');

  const catalog = pick<Record<string, unknown>>(status.data, 'Catalog');
  const features = pick<Record<string, boolean>>(status.data, 'Features') ?? {};
  const enabled = pick<boolean>(status.data, 'Enabled');

  return (
    <>
      {!!snap?.problems.length && (
        <Section title="Degraded readings" subtitle="The engine could not read these this tick; everything else on this page is still accurate.">
          <ul className="obs-problems">
            {snap.problems.map((p) => <li key={p} className="obs-mono obs-small">{p}</li>)}
          </ul>
        </Section>
      )}

      <Section title="Engine" subtitle={`${snap?.plugin ?? 'Orca Engine'} ${snap?.version ?? ''} · schema v${snap?.schemaVersion ?? '?'}`}>
        <Tiles>
          <Tile label="Status" value={enabled === false ? 'Disabled' : 'Running'} tone={enabled === false ? 'bad' : 'ok'} />
          <Tile label="Uptime" value={fmtUptime(snap?.uptimeMinutes)} />
          <Tile label="Home build" value={fmtMs(build.avgMs)} hint={`${build.count} builds`} />
          <Tile
            label="Cache hit rate"
            value={hitRate === undefined ? '—' : `${hitRate.toFixed(0)}%`}
            hint="home bundles served from cache"
            tone={hitRate !== undefined && hitRate < 40 ? 'warn' : undefined}
          />
        </Tiles>
      </Section>

      <Section title="Resources" subtitle={snap?.process.scope}>
        <Tiles>
          <Tile label="CPU" value={cpu === undefined ? '—' : `${cpu.toFixed(1)}%`} hint="whole Jellyfin process" />
          <Tile label="Working set" value={fmtBytes(snap?.process.workingSetBytes)} hint="whole Jellyfin process" />
          <Tile label="GC heap" value={fmtBytes(snap?.process.gcHeapBytes)} hint="whole Jellyfin process" />
          <Tile label="Engine database" value={fmtBytes(snap?.engine.databaseBytes)} hint="the engine's own SQLite file" />
        </Tiles>
      </Section>

      <Section title="Throughput" subtitle="Rates are computed by diffing counter snapshots while this page is open.">
        <div className="obs-sparks">
          <SparkStat label="Home built" value={`${homeBuilds.toFixed(2)}/s`} points={series(samples, 'home.built')} />
          <SparkStat label="Home from cache" value={`${cacheHits.toFixed(2)}/s`} points={series(samples, 'home.cache_hit')} />
          <SparkStat label="Background refreshes" value={`${rate(samples, 'home.refreshed').toFixed(2)}/s`} points={series(samples, 'home.refreshed')} />
        </div>
      </Section>

      {status.error && <Problem error={status.error} />}
      {catalog && (
        <Section title="Catalog">
          <Tiles>
            <Tile label="Total items" value={String(pick(catalog, 'Total') ?? 0)} />
            <Tile label="External rows" value={String(pick(catalog, 'ExternalRows') ?? 0)} hint="not in the library — discovery growth" />
            <Tile label="Behavior events" value={String(pick(status.data, 'BehaviorEvents') ?? 0)} />
            <Tile label="Profiles" value={String(pick(status.data, 'Profiles') ?? 0)} />
          </Tiles>
          <div className="obs-chips">
            {Object.entries(features).filter(([, on]) => on).map(([name]) => (
              <span key={name} className="obs-chip">{name}</span>
            ))}
          </div>
        </Section>
      )}
    </>
  );
}

function SparkStat({ label, value, points }: { label: string; value: string; points: number[] }) {
  return (
    <div className="obs-sparkstat">
      <div className="obs-tile-label">{label}</div>
      <div className="obs-sparkstat-body">
        <span className="obs-tile-value">{value}</span>
        <Spark points={points} />
      </div>
    </div>
  );
}

export function EngineHealth({ snap, samples }: { snap: Snapshot | undefined; samples: Sample[] }) {
  const c = snap?.counters ?? {};
  const hosts = discover(c, 'http.');
  const maintenance = timing(c, 'maintenance.tick');
  const maintenanceErrors = c['maintenance.tick.error'] ?? 0;

  return (
    <>
      <Section title="Integrations">
        <div className="obs-chips">
          <Badge on={!!snap?.integrations.tmdbConfigured}>TMDB</Badge>
          <Badge on={!!snap?.integrations.arrConfigured}>Sonarr / Radarr</Badge>
          <Badge on={!!snap?.integrations.jellyseerrConfigured}>Jellyseerr</Badge>
        </div>
      </Section>

      <Section
        title="Outbound calls"
        subtitle="Every external call the engine makes, timed at the HTTP layer and grouped by host."
      >
        <Table head={['Host', 'Calls', 'Avg', 'Errors', 'Error rate', 'Rate']} empty={hosts.length === 0}>
          {hosts.map((host) => {
            const t = timing(c, `http.${host}`);
            const errors = c[`http.${host}.error`] ?? 0;
            const errPct = t.count > 0 ? (errors / t.count) * 100 : 0;
            return (
              <tr key={host}>
                <td className="obs-mono">{host}</td>
                <td>{t.count}</td>
                <td className={t.avgMs > 2000 ? 'obs-warn' : undefined}>{fmtMs(t.avgMs)}</td>
                <td className={errors > 0 ? 'obs-bad' : undefined}>{errors}</td>
                <td>{errPct.toFixed(1)}%</td>
                <td><Spark points={series(samples, `http.${host}.count`)} width={90} height={22} /></td>
              </tr>
            );
          })}
        </Table>
      </Section>

      <Section title="Queues" subtitle="Backlogs that would otherwise only show up as memory growth.">
        <div className="obs-stack">
          <div>
            <div className="obs-tile-label">Trailer queue</div>
            <Meter value={snap?.engine.trailerQueueDepth ?? 0} max={500} caption={`${snap?.engine.trailerQueueDepth ?? 0} of 500 queued`} />
          </div>
          <div>
            <div className="obs-tile-label">Profile recompute</div>
            <Meter
              value={snap?.engine.recomputeQueueDepth ?? 0}
              max={Math.max(50, (snap?.engine.recomputeQueueDepth ?? 0) * 2)}
              caption={`${snap?.engine.recomputeQueueDepth ?? 0} waiting (unbounded queue)`}
            />
          </div>
        </div>
      </Section>

      <Section title="Maintenance loop" subtitle="Runs every 15 minutes; a discovery cycle every 8th tick.">
        <Tiles>
          <Tile label="Ticks completed" value={maintenance.count} />
          <Tile label="Average tick" value={fmtMs(maintenance.avgMs)} />
          <Tile
            label="Failed ticks"
            value={maintenanceErrors}
            tone={maintenanceErrors > 0 ? 'bad' : 'ok'}
            hint={maintenanceErrors > 0 ? 'see Live Logs for the exception' : undefined}
          />
        </Tiles>
      </Section>
    </>
  );
}

export function Performance({ snap }: { snap: Snapshot | undefined }) {
  const c = snap?.counters;
  const rows = discover(c, 'home.row.');
  const stages = ['home.settings', 'home.affinity', 'home.build', 'home.serve_fresh', 'home.serve_cached'];

  const rowTimings = rows
    .map((name) => {
      const t = timing(c, `home.row.${name}`);
      return {
        name,
        ...t,
        timeouts: c?.[`home.row.${name}.timeout`] ?? 0,
        errors: c?.[`home.row.${name}.error`] ?? 0,
      };
    })
    .sort((a, b) => b.avgMs - a.avgMs);

  return (
    <>
      <Section title="Home pipeline" subtitle="Serve-fresh is the number a cold viewer actually waits for.">
        <Tiles>
          {stages.map((s) => {
            const t = timing(c, s);
            return <Tile key={s} label={s.replace('home.', '')} value={fmtMs(t.avgMs)} hint={`${t.count} calls`} />;
          })}
        </Tiles>
      </Section>

      <Section
        title="Row cost"
        subtitle="Rows are built concurrently, so the page costs the slowest row — not their sum. This is that row."
      >
        {rowTimings.length > 0 && (
          <Bars rows={rowTimings.map((r) => ({ label: r.name, value: r.avgMs, note: fmtMs(r.avgMs) }))} />
        )}
        <Table head={['Row', 'Builds', 'Average', 'Total', 'Timeouts', 'Errors']} empty={rowTimings.length === 0}>
          {rowTimings.map((r) => (
            <tr key={r.name}>
              <td className="obs-mono">{r.name}</td>
              <td>{r.count}</td>
              <td>{fmtMs(r.avgMs)}</td>
              <td>{fmtMs(r.totalMs)}</td>
              <td className={r.timeouts > 0 ? 'obs-warn' : undefined}>{r.timeouts}</td>
              <td className={r.errors > 0 ? 'obs-bad' : undefined}>{r.errors}</td>
            </tr>
          ))}
        </Table>
      </Section>
    </>
  );
}

export function Cache({ snap, samples }: { snap: Snapshot | undefined; samples: Sample[] }) {
  const c = snap?.counters ?? {};
  const namespaces = [...new Set(
    Object.keys(c)
      .filter((k) => k.startsWith('cache.') && (k.endsWith('.hit') || k.endsWith('.miss')))
      .map((k) => k.split('.')[1]),
  )].sort();

  return (
    <>
      <Section title="Cache" subtitle={`Bounded at ${snap?.engine.cacheEntryLimit ?? 4096} entries, private to the engine.`}>
        <Table head={['Namespace', 'Hits', 'Misses', 'Hit rate', 'Recent']} empty={namespaces.length === 0}>
          {namespaces.map((ns) => {
            const hits = c[`cache.${ns}.hit`] ?? 0;
            const misses = c[`cache.${ns}.miss`] ?? 0;
            const total = hits + misses;
            const pct = total > 0 ? (hits / total) * 100 : 0;
            return (
              <tr key={ns}>
                <td className="obs-mono">{ns}</td>
                <td>{hits}</td>
                <td>{misses}</td>
                <td className={pct < 40 && total > 20 ? 'obs-warn' : undefined}>{pct.toFixed(0)}%</td>
                <td><Spark points={series(samples, `cache.${ns}.hit`)} width={90} height={22} /></td>
              </tr>
            );
          })}
        </Table>
      </Section>
    </>
  );
}
