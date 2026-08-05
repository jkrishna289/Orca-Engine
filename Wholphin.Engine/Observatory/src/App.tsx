import { useEffect, useMemo, useState } from 'react';
import { usePoll, useHistory } from './api';
import { Problem, Boundary } from './ui';
import { Overview, EngineHealth, Performance, Cache, toSnapshot, type Snapshot } from './pages-core';
import { Metadata, TrailerResolver, Recommendations, LiveLogs, Timeline, Users, Settings } from './pages-detail';

const PAGES = [
  'Overview', 'Engine Health', 'Performance', 'Metadata', 'Trailer Resolver',
  'Recommendation Engine', 'Cache', 'Live Logs', 'Timeline', 'Users', 'Settings',
] as const;

type Page = typeof PAGES[number];

export default function App() {
  const [page, setPage] = useState<Page>('Overview');

  // The one poll everything shares. 5s is cheap: the endpoint is in-memory reads plus a single
  // stat() of the database file — no queries. Admin/Status and Admin/Diagnostics hit the database
  // and are polled at a minute, by the pages that need them.
  const { data: raw, error } = usePoll<unknown>('OrcaEngine/Observatory/Snapshot', 5000);
  const snap = useMemo(() => toSnapshot(raw), [raw]);
  const [snapshots, setSnapshots] = useState<Snapshot[]>([]);
  const samples = useHistory(snap?.counters);

  // Kept alongside the counter history so the Overview page can turn two processor-time readings
  // into a CPU rate. Each poll returns a fresh object, so identity is a sufficient guard.
  useEffect(() => {
    if (!snap) return;
    setSnapshots((prev) => (prev[prev.length - 1] === snap ? prev : [...prev, snap].slice(-120)));
  }, [snap]);

  return (
    <div className="obs-root">
      <header className="obs-head">
        <div className="obs-brand">
          <span className="obs-brand-mark" aria-hidden="true">◎</span>
          <div>
            <h1>Orca Observatory</h1>
            <p className="obs-muted obs-small">
              {snap ? `${snap.plugin} ${snap.version}` : 'Connecting…'}
            </p>
          </div>
        </div>
      </header>

      <nav className="obs-nav" aria-label="Observatory sections">
        {PAGES.map((name) => (
          <button
            key={name}
            className={`obs-navitem${page === name ? ' obs-navitem-active' : ''}`}
            aria-current={page === name ? 'page' : undefined}
            onClick={() => setPage(name)}
          >
            {name}
          </button>
        ))}
      </nav>

      <main className="obs-main">
        {error && <Problem error={error} />}
        {/* Keyed on the page so a crash on one tab doesn't leave the boundary latched when you
            navigate away. A dashboard that goes blank tells you nothing about the engine. */}
        <Boundary key={page}>
          {page === 'Overview' && <Overview snap={snap} samples={samples} snapshots={snapshots} />}
          {page === 'Engine Health' && <EngineHealth snap={snap} samples={samples} />}
          {page === 'Performance' && <Performance snap={snap} />}
          {page === 'Metadata' && <Metadata snap={snap} />}
          {page === 'Trailer Resolver' && <TrailerResolver />}
          {page === 'Recommendation Engine' && <Recommendations />}
          {page === 'Cache' && <Cache snap={snap} samples={samples} />}
          {page === 'Live Logs' && <LiveLogs />}
          {page === 'Timeline' && <Timeline />}
          {page === 'Users' && <Users snap={snap} />}
          {page === 'Settings' && <Settings />}
        </Boundary>
      </main>
    </div>
  );
}
