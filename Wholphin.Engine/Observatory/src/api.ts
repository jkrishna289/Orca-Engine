import { useCallback, useEffect, useRef, useState } from 'react';

/** Jellyfin's global dashboard client. Present because we render inside its SPA. */
declare const ApiClient: {
  getUrl(path: string, params?: Record<string, unknown>): string;
  accessToken(): string;
};

/**
 * Jellyfin's MVC stack content-negotiates between camelCase and PascalCase output formatters, so
 * which one a given endpoint returns is not something the client should have to be sure about.
 */
export function pick<T = unknown>(source: unknown, key: string): T | undefined {
  if (!source || typeof source !== 'object') return undefined;
  const record = source as Record<string, unknown>;
  if (key in record) return record[key] as T;
  const lower = key.charAt(0).toLowerCase() + key.slice(1);
  if (lower in record) return record[lower] as T;
  const upper = key.charAt(0).toUpperCase() + key.slice(1);
  return record[upper] as T | undefined;
}

export async function get<T>(path: string, params?: Record<string, unknown>): Promise<T> {
  const response = await fetch(ApiClient.getUrl(path, params), {
    headers: { 'X-Emby-Token': ApiClient.accessToken(), Accept: 'application/json' },
  });
  if (!response.ok) {
    throw new Error(
      response.status === 403
        ? 'Administrator access required.'
        : `${path} failed (${response.status})`,
    );
  }
  return (await response.json()) as T;
}

export async function post(path: string, params?: Record<string, unknown>): Promise<void> {
  const response = await fetch(ApiClient.getUrl(path, params), {
    method: 'POST',
    headers: { 'X-Emby-Token': ApiClient.accessToken() },
  });
  if (!response.ok) throw new Error(`${path} failed (${response.status})`);
}

export interface Fetched<T> {
  data: T | undefined;
  error: string | undefined;
  loading: boolean;
  reload: () => void;
}

/**
 * Fetches on mount and then every `intervalMs`, stopping while the browser tab is hidden and
 * tearing the timer down on unmount. Both matter: the dashboard is a long-lived SPA, and a timer
 * that outlives its page keeps hitting the server forever.
 */
export function usePoll<T>(path: string, intervalMs = 0, params?: Record<string, unknown>): Fetched<T> {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [nonce, setNonce] = useState(0);
  const key = JSON.stringify(params ?? null);

  useEffect(() => {
    let cancelled = false;

    const run = () => {
      if (document.hidden) return;
      get<T>(path, params ? JSON.parse(key) : undefined)
        .then((result) => {
          if (cancelled) return;
          setData(result);
          setError(undefined);
        })
        .catch((e: Error) => !cancelled && setError(e.message))
        .finally(() => !cancelled && setLoading(false));
    };

    run();
    if (intervalMs <= 0) return () => { cancelled = true; };

    const timer = window.setInterval(run, intervalMs);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path, key, intervalMs, nonce]);

  const reload = useCallback(() => setNonce((n) => n + 1), []);
  return { data, error, loading, reload };
}

/** A point-in-time counter snapshot, kept so the UI can turn monotonic totals into rates. */
export interface Sample {
  at: number;
  counters: Record<string, number>;
}

/**
 * Keeps a rolling window of counter snapshots.
 *
 * This is where every rate and time-axis chart comes from. The server's counters are monotonic
 * totals with no clock, so rather than retaining time series server-side, the page diffs successive
 * polls — real rates, for as long as the page stays open, at zero cost to the engine.
 */
export function useHistory(counters: Record<string, number> | undefined, limit = 120): Sample[] {
  const [samples, setSamples] = useState<Sample[]>([]);
  const seen = useRef<Record<string, number>>();

  useEffect(() => {
    if (!counters || counters === seen.current) return;
    seen.current = counters;
    setSamples((prev) => [...prev, { at: Date.now(), counters }].slice(-limit));
  }, [counters, limit]);

  return samples;
}

/** Per-second rate of a counter across the sample window, or 0 when there isn't enough history. */
export function rate(samples: Sample[], key: string): number {
  if (samples.length < 2) return 0;
  const first = samples[0];
  const last = samples[samples.length - 1];
  const seconds = (last.at - first.at) / 1000;
  if (seconds <= 0) return 0;
  const delta = (last.counters[key] ?? 0) - (first.counters[key] ?? 0);
  return delta <= 0 ? 0 : delta / seconds;
}

/** The per-sample deltas of a counter — the series behind a sparkline. */
export function series(samples: Sample[], key: string): number[] {
  const out: number[] = [];
  for (let i = 1; i < samples.length; i++) {
    out.push(Math.max(0, (samples[i].counters[key] ?? 0) - (samples[i - 1].counters[key] ?? 0)));
  }
  return out;
}

export interface EngineEvent {
  seq: number;
  at: string;
  level: string;
  component: string;
  event: string;
  elapsedMs: number | null;
  data: string | null;
  exception: string | null;
}

/**
 * Normalizes one event.
 *
 * The two sources disagree on casing by construction: the SSE frames are serialized camelCase by
 * this plugin, while `Observatory/Events` goes through Jellyfin's formatter and comes back
 * PascalCase. Reading both case-insensitively is cheaper than making the server lie about one.
 */
function toEvent(raw: unknown): EngineEvent {
  const ms = pick(raw, 'ElapsedMs');
  return {
    seq: Number(pick(raw, 'Seq') ?? 0),
    at: String(pick(raw, 'At') ?? new Date().toISOString()),
    level: String(pick(raw, 'Level') ?? 'info'),
    component: String(pick(raw, 'Component') ?? '?'),
    event: String(pick(raw, 'Event') ?? '?'),
    elapsedMs: ms === null || ms === undefined ? null : Number(ms),
    data: (pick<string>(raw, 'Data') ?? null) as string | null,
    exception: (pick<string>(raw, 'Exception') ?? null) as string | null,
  };
}

/**
 * Live event stream.
 *
 * Uses fetch + a stream reader rather than EventSource because EventSource cannot set headers —
 * authenticating it would mean putting the Jellyfin token in the query string, and tokens do not
 * belong in URLs.
 */
export function useEventStream(paused: boolean, capacity = 1000) {
  const [events, setEvents] = useState<EngineEvent[]>([]);
  const [connected, setConnected] = useState(false);
  const cursor = useRef(0);

  useEffect(() => {
    if (paused) return;
    const abort = new AbortController();
    let stopped = false;

    (async () => {
      // Seed from the ring so the view isn't empty until something happens.
      try {
        const recent = await get<unknown[]>('OrcaEngine/Observatory/Events', { limit: 300 });
        if (stopped) return;
        const ordered = recent.map(toEvent).sort((a, b) => a.seq - b.seq);
        cursor.current = ordered.length ? ordered[ordered.length - 1].seq : 0;
        setEvents(ordered);
      } catch {
        /* the stream below is the real source; a failed seed just means starting empty */
      }

      while (!stopped) {
        try {
          const response = await fetch(
            ApiClient.getUrl('OrcaEngine/Observatory/Stream', { after: cursor.current }),
            {
              headers: { 'X-Emby-Token': ApiClient.accessToken(), Accept: 'text/event-stream' },
              signal: abort.signal,
            },
          );
          if (!response.ok || !response.body) throw new Error(`stream ${response.status}`);
          setConnected(true);

          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';

          for (;;) {
            const { done, value } = await reader.read();
            if (done || stopped) break;
            buffer += decoder.decode(value, { stream: true });

            // SSE frames are separated by a blank line.
            let split: number;
            while ((split = buffer.indexOf('\n\n')) >= 0) {
              const frame = buffer.slice(0, split);
              buffer = buffer.slice(split + 2);
              const payload = frame
                .split('\n')
                .filter((line) => line.startsWith('data:'))
                .map((line) => line.slice(5).trim())
                .join('');
              if (!payload) continue;
              try {
                const parsed = toEvent(JSON.parse(payload));
                cursor.current = Math.max(cursor.current, parsed.seq);
                setEvents((prev) => [...prev, parsed].slice(-capacity));
              } catch {
                /* ignore a partial frame */
              }
            }
          }
        } catch {
          if (stopped) break;
          setConnected(false);
          // The server restarted or the connection dropped; retry rather than going dark.
          await new Promise((r) => setTimeout(r, 3000));
        }
      }
    })();

    return () => {
      stopped = true;
      setConnected(false);
      abort.abort();
    };
  }, [paused, capacity]);

  return { events, connected, clear: () => setEvents([]) };
}
