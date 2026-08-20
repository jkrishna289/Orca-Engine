import { useEffect, useMemo, useState } from 'react';
import { get, postJson, post } from './api';
import { Section, Problem, Empty } from './ui';
import { KNOWN_KEYS, PLUGIN_ID, groupsFor, inferField, type Field, type Group, type SettingsTab } from './settings-schema';

type Config = Record<string, unknown>;

/**
 * The settings belonging to one page, with its own save bar.
 *
 * Reads and writes through Jellyfin's own plugin configuration API rather than an endpoint of the
 * engine's own — that API is already admin-gated and already the thing Jellyfin persists, so there
 * is nothing here for a second implementation to disagree with.
 *
 * Each mounted panel holds its own copy of the configuration and posts it whole, because Jellyfin
 * replaces the stored object rather than merging. That is safe here only because one page is mounted
 * at a time and each loads fresh on mount — a panel that stayed alive in the background would post a
 * stale copy over someone else's save.
 */
export function SettingsPanel({ tab, groups, extras: showExtras = false, operations = false }: {
  tab?: SettingsTab;
  groups?: Group[];
  extras?: boolean;
  operations?: boolean;
}) {
  const shown = groups ?? groupsFor(tab ?? 'general');
  const [saved, setSaved] = useState<Config>();
  const [draft, setDraft] = useState<Config>();
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [reveal, setReveal] = useState<Set<string>>(new Set());

  useEffect(() => { void load(); }, []);

  async function load() {
    setError(undefined);
    try {
      const config = await get<Config>(`Plugins/${PLUGIN_ID}/Configuration`);
      setSaved(config);
      setDraft({ ...config });
    } catch (e) {
      setError((e as Error).message);
    }
  }

  const dirty = useMemo(
    () => (!saved || !draft ? [] : Object.keys(draft).filter((k) => !Object.is(draft[k], saved[k]))),
    [draft, saved],
  );

  async function save() {
    if (!draft) return;
    setBusy(true);
    setStatus(undefined);
    try {
      // The whole object goes back, not just the edits: Jellyfin replaces the stored configuration
      // wholesale, so posting a partial one would silently reset everything absent to its default.
      await postJson(`Plugins/${PLUGIN_ID}/Configuration`, draft);
      setSaved({ ...draft });
      setStatus(`Saved ${dirty.length} change${dirty.length === 1 ? '' : 's'}.`);
    } catch (e) {
      setStatus(`Save failed: ${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  }

  if (error) {
    return (
      <>
        <Problem error={error} />
        <Section title="Settings"><button className="obs-btn" onClick={() => void load()}>Retry</button></Section>
      </>
    );
  }

  if (!draft || !saved) return <Empty>Loading configuration…</Empty>;

  const set = (key: string, value: unknown) => setDraft((d) => ({ ...(d ?? {}), [key]: value }));

  const extras = showExtras
    ? Object.keys(draft)
        .filter((k) => !KNOWN_KEYS.has(k))
        .sort()
        .map((k) => inferField(k, draft[k]))
    : [];

  if (shown.length === 0 && extras.length === 0 && !operations) {
    return null;
  }

  return (
    <>
      <div className="obs-savebar">
        <div>
          <strong>{dirty.length ? `${dirty.length} unsaved change${dirty.length === 1 ? '' : 's'}` : 'No unsaved changes'}</strong>
          {!!dirty.length && <div className="obs-muted obs-small obs-mono">{dirty.join(', ')}</div>}
          {status && <div className="obs-muted obs-small">{status}</div>}
        </div>
        <div className="obs-actions">
          <button className="obs-btn" disabled={!dirty.length || busy} onClick={() => setDraft({ ...saved })}>Discard</button>
          <button className="obs-btn obs-btn-primary" disabled={!dirty.length || busy} onClick={() => void save()}>
            {busy ? 'Saving…' : 'Save changes'}
          </button>
        </div>
      </div>

      {shown.map((group) => (
        <Section key={group.title} title={group.title} subtitle={group.blurb}>
          <div className="obs-fields">
            {group.fields.map((field) => (
              <FieldRow
                key={field.key}
                field={field}
                value={draft[field.key]}
                changed={dirty.includes(field.key)}
                revealed={reveal.has(field.key)}
                onReveal={() => setReveal((r) => { const n = new Set(r); n.has(field.key) ? n.delete(field.key) : n.add(field.key); return n; })}
                onChange={(v) => set(field.key, v)}
              />
            ))}
          </div>
        </Section>
      ))}

      {extras.length > 0 && (
        <Section
          title="Other settings"
          subtitle="Present in the engine's configuration but not described by this dashboard — shown so a newly added setting is never stranded."
        >
          <div className="obs-fields">
            {extras.map((field) => (
              <FieldRow
                key={field.key}
                field={field}
                value={draft[field.key]}
                changed={dirty.includes(field.key)}
                revealed={reveal.has(field.key)}
                onReveal={() => setReveal((r) => { const n = new Set(r); n.has(field.key) ? n.delete(field.key) : n.add(field.key); return n; })}
                onChange={(v) => set(field.key, v)}
              />
            ))}
          </div>
        </Section>
      )}

      {operations && <Operations />}
    </>
  );
}

/**
 * The Settings page: what belongs to no particular screen, anything the dashboard does not describe,
 * and the destructive operations. Everything else now lives beside the thing it configures.
 */
export function Settings() {
  return (
    <>
      <Section
        title="Settings"
        subtitle="Engine-wide options and maintenance. Settings for a specific subsystem now live on that subsystem's own page — trailers under Trailer Resolver, embeddings under Embeddings, and so on."
      >
        <Empty>
          Looking for something else? Home rows, discovery and AI are under <b>Recommendation
          Engine</b>; provider keys under <b>Metadata</b>; peers and ports under <b>Torrent
          Streaming</b>.
        </Empty>
      </Section>

      <SettingsPanel tab="general" extras operations />
    </>
  );
}

function FieldRow({ field, value, changed, revealed, onReveal, onChange }: {
  field: Field;
  value: unknown;
  changed: boolean;
  revealed: boolean;
  onReveal: () => void;
  onChange: (value: unknown) => void;
}) {
  const id = `cfg-${field.key}`;

  if (field.kind === 'bool') {
    return (
      <div className={`obs-field-row obs-field-bool${changed ? ' obs-field-changed' : ''}`}>
        <label htmlFor={id} className="obs-switch">
          <input id={id} type="checkbox" checked={value === true} onChange={(e) => onChange(e.target.checked)} />
          <span>{field.label}</span>
        </label>
        {field.help && <p className="obs-field-help">{field.help}</p>}
      </div>
    );
  }

  return (
    <div className={`obs-field-row${changed ? ' obs-field-changed' : ''}`}>
      <label htmlFor={id} className="obs-field-label">{field.label}</label>

      {field.kind === 'select' ? (
        <select id={id} className="obs-select obs-field-control" value={String(value ?? '')} onChange={(e) => onChange(e.target.value)}>
          {field.options?.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
      ) : field.kind === 'int' || field.kind === 'float' ? (
        <input
          id={id}
          className="obs-input obs-field-control"
          type="number"
          value={Number(value ?? 0)}
          min={field.min}
          max={field.max}
          step={field.step ?? (field.kind === 'int' ? 1 : 0.01)}
          onChange={(e) => {
            const n = field.kind === 'int' ? parseInt(e.target.value, 10) : parseFloat(e.target.value);
            onChange(Number.isNaN(n) ? 0 : n);
          }}
        />
      ) : (
        <span className="obs-field-control obs-secret">
          <input
            id={id}
            className="obs-input"
            // Secrets stay masked until asked for, so a shoulder or a screenshot does not leak a key.
            type={field.kind === 'secret' && !revealed ? 'password' : 'text'}
            value={String(value ?? '')}
            placeholder={field.placeholder}
            autoComplete={field.kind === 'secret' ? 'off' : undefined}
            spellCheck={false}
            onChange={(e) => onChange(e.target.value)}
          />
          {field.kind === 'secret' && (
            <button type="button" className="obs-btn obs-btn-small" onClick={onReveal}>
              {revealed ? 'Hide' : 'Show'}
            </button>
          )}
        </span>
      )}

      {field.help && <p className="obs-field-help">{field.help}</p>}
    </div>
  );
}

const OPERATIONS: { label: string; path: string; params?: Record<string, unknown>; note?: string }[] = [
  { label: 'Resync library', path: 'OrcaEngine/Catalog/Resync' },
  { label: 'Reconcile availability', path: 'OrcaEngine/Catalog/Reconcile' },
  { label: 'Enrich from TMDB', path: 'OrcaEngine/Catalog/EnrichTmdb', params: { maxItems: 100 } },
  { label: 'Pull global discovery', path: 'OrcaEngine/Discovery/PullGlobal' },
  { label: 'Recompute Orca ratings', path: 'OrcaEngine/Analytics/CommunityRating/Recompute' },
  { label: 'Purge unjustified rows', path: 'OrcaEngine/Catalog/PurgeUnjustified', note: 'Deletes external catalog rows with no live justification.' },
];

function Operations() {
  const [busy, setBusy] = useState<string>();
  const [message, setMessage] = useState<string>();

  const run = async (op: typeof OPERATIONS[number]) => {
    if (op.note && !window.confirm(`${op.label}\n\n${op.note}\n\nContinue?`)) return;
    setBusy(op.label);
    setMessage(undefined);
    try {
      await post(op.path, op.params);
      setMessage(`${op.label}: started.`);
    } catch (e) {
      setMessage(`${op.label}: ${(e as Error).message}`);
    } finally {
      setBusy(undefined);
    }
  };

  const resetMetrics = async () => {
    if (!window.confirm('Reset all counters to zero? Timing averages and totals are lost, and every rate chart restarts from empty.')) return;
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
      <Section title="Operations" subtitle="Run a maintenance pass now instead of waiting for the next cycle.">
        <div className="obs-actions obs-wrap">
          {OPERATIONS.map((op) => (
            <button key={op.label} className="obs-btn" disabled={!!busy} onClick={() => void run(op)}>
              {busy === op.label ? 'Working…' : op.label}
            </button>
          ))}
        </div>
        {message && <p className="obs-note">{message}</p>}
      </Section>

      <Section title="Danger zone">
        <button className="obs-btn obs-btn-danger" disabled={!!busy} onClick={() => void resetMetrics()}>
          Reset all counters
        </button>
      </Section>
    </>
  );
}
