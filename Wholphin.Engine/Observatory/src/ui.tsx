import { Component, type ErrorInfo, type ReactNode } from 'react';
import type { EngineAlert } from './api';

/**
 * Catches a render crash and shows it, instead of unmounting the tree.
 *
 * React 18 tears the whole root down on an uncaught render error — which for a self-hosted
 * diagnostics page means a blank screen and no way to tell a crash from a script that never loaded.
 * Showing the failure is the entire point of this tool.
 */
export class Boundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state: { error: Error | null } = { error: null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[OrcaObservatory]', error, info.componentStack);
  }

  render() {
    const { error } = this.state;
    if (!error) return this.props.children;
    return (
      <div className="obs-section">
        <h2>This panel failed to render</h2>
        <p className="obs-muted obs-small">
          The rest of the dashboard still works. Full detail is in the browser console.
        </p>
        <pre className="obs-json">{String(error.stack ?? error)}</pre>
      </div>
    );
  }
}

export function Section({ title, subtitle, children, actions }: {
  title: string;
  subtitle?: string;
  children: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <section className="obs-section">
      <header className="obs-section-head">
        <div>
          <h2>{title}</h2>
          {subtitle && <p className="obs-muted">{subtitle}</p>}
        </div>
        {actions}
      </header>
      {children}
    </section>
  );
}

export function Tiles({ children }: { children: ReactNode }) {
  return <div className="obs-tiles">{children}</div>;
}

export function Tile({ label, value, hint, tone }: {
  label: string;
  value: ReactNode;
  hint?: string;
  tone?: 'ok' | 'warn' | 'bad';
}) {
  return (
    <div className={`obs-tile${tone ? ` obs-${tone}` : ''}`}>
      <div className="obs-tile-label">{label}</div>
      <div className="obs-tile-value">{value}</div>
      {hint && <div className="obs-tile-hint">{hint}</div>}
    </div>
  );
}

export function Badge({ on, children }: { on: boolean; children: ReactNode }) {
  return <span className={`obs-badge ${on ? 'obs-ok' : 'obs-off'}`}>{children}</span>;
}

export function Table({ head, children, empty }: { head: string[]; children: ReactNode; empty?: boolean }) {
  return (
    <div className="obs-tablewrap">
      <table className="obs-table">
        <thead>
          <tr>{head.map((h) => <th key={h}>{h}</th>)}</tr>
        </thead>
        <tbody>
          {empty
            ? <tr><td colSpan={head.length} className="obs-muted obs-center">Nothing recorded yet.</td></tr>
            : children}
        </tbody>
      </table>
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="obs-empty">{children}</p>;
}

export function Problem({ error }: { error: string }) {
  return <p className="obs-problem">{error}</p>;
}

/**
 * A bar showing a value against a known ceiling — the only honest way to show "how full is it".
 *
 * Fullness picks the colour by default, because for a cache or a queue "nearly full" is bad news.
 * Pass `tone` for the bars where it is the opposite: a progress bar at 100% has finished, not failed.
 */
export function Meter({ value, max, caption, tone: fixedTone }: {
  value: number;
  max: number;
  caption?: string;
  tone?: 'ok' | 'warn' | 'bad';
}) {
  const pct = max > 0 ? Math.min(100, (value / max) * 100) : 0;
  const tone = fixedTone ?? (pct > 90 ? 'bad' : pct > 60 ? 'warn' : 'ok');
  return (
    <div className="obs-meter">
      <div className="obs-meter-track">
        <div className={`obs-meter-fill obs-${tone}-bg`} style={{ width: `${pct}%` }} />
      </div>
      <span className="obs-muted obs-small">{caption ?? `${value} / ${max}`}</span>
    </div>
  );
}

/** Inline sparkline. A charting dependency for this would be ~90 KB to draw a polyline. */
export function Spark({ points, width = 140, height = 32 }: { points: number[]; width?: number; height?: number }) {
  if (points.length < 2) return <svg width={width} height={height} className="obs-spark" aria-hidden="true" />;
  const max = Math.max(...points, 1);
  const step = width / (points.length - 1);
  const path = points.map((p, i) => `${i * step},${height - (p / max) * (height - 2) - 1}`).join(' ');
  return (
    <svg width={width} height={height} className="obs-spark" role="img" aria-label={`peak ${max}`}>
      <polyline points={path} fill="none" stroke="currentColor" strokeWidth="1.5" />
    </svg>
  );
}

/** Horizontal bar chart — used for "which row is slow", the question concurrency created. */
export function Bars({ rows }: { rows: { label: string; value: number; note?: string }[] }) {
  const max = Math.max(...rows.map((r) => r.value), 1);
  return (
    <div className="obs-bars">
      {rows.map((r) => (
        <div key={r.label} className="obs-bar-row">
          <span className="obs-bar-label" title={r.label}>{r.label}</span>
          <span className="obs-bar-track">
            <span className="obs-bar-fill" style={{ width: `${(r.value / max) * 100}%` }} />
          </span>
          <span className="obs-bar-value">{r.note ?? r.value}</span>
        </div>
      ))}
    </div>
  );
}

export const fmtBytes = (n: number | undefined): string => {
  if (!n) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = n;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value >= 10 || unit === 0 ? 0 : 1)} ${units[unit]}`;
};

export const fmtMs = (n: number | undefined | null): string => {
  if (n === undefined || n === null) return '—';
  return n >= 1000 ? `${(n / 1000).toFixed(2)} s` : `${Math.round(n)} ms`;
};

export const fmtUptime = (minutes: number | undefined): string => {
  if (!minutes) return '—';
  if (minutes < 60) return `${Math.round(minutes)}m`;
  const h = Math.floor(minutes / 60);
  if (h < 24) return `${h}h ${Math.round(minutes % 60)}m`;
  return `${Math.floor(h / 24)}d ${h % 24}h`;
};

/** Averages a `{prefix}.count` / `{prefix}.total_ms` pair — the engine's timing convention. */
export function timing(counters: Record<string, number> | undefined, prefix: string) {
  const count = counters?.[`${prefix}.count`] ?? 0;
  const totalMs = counters?.[`${prefix}.total_ms`] ?? 0;
  return { count, totalMs, avgMs: count > 0 ? totalMs / count : 0 };
}

/** Every distinct `{prefix}.{name}.count` key under a namespace, so new subsystems appear unbidden. */
export function discover(counters: Record<string, number> | undefined, prefix: string): string[] {
  if (!counters) return [];
  const found = new Set<string>();
  for (const key of Object.keys(counters)) {
    if (key.startsWith(prefix) && key.endsWith('.count')) {
      found.add(key.slice(prefix.length, -'.count'.length));
    }
  }
  return [...found].sort();
}

/**
 * Standing health conditions, pinned above the navigation on every page.
 *
 * This exists because the failure it was built for is invisible by construction: when a cloud
 * embedding provider fails the engine falls back to local TF-IDF and everything keeps working, just
 * much worse. A line in the live log is no use — nobody is watching the live log at 3am. So the
 * server holds the condition until it stops being true, and it sits here, unmissable, until then.
 */
export function AlertBanner({ alerts }: { alerts: EngineAlert[] }) {
  if (!alerts.length) return null;
  return (
    <div className="obs-alerts" role="alert" aria-live="assertive">
      {alerts.map((a) => {
        const critical = a.level === 'critical';
        return (
          <div key={a.key} className={`obs-alert obs-alert-${critical ? 'critical' : 'warn'}`}>
            <span className="obs-alert-tag">{critical ? 'CRITICAL' : 'WARNING'}</span>
            <div className="obs-alert-body">
              <strong>{a.title}</strong>
              {a.detail && <p>{a.detail}</p>}
              <p className="obs-alert-meta">
                since {a.firstSeenUtc ? new Date(a.firstSeenUtc).toLocaleString() : 'unknown'}
                {a.count > 1 && ` · seen ${a.count} times`}
              </p>
            </div>
          </div>
        );
      })}
    </div>
  );
}
