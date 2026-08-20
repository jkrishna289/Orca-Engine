import { useEffect, useRef, useState } from 'react';
import { usePoll, get, pick } from './api';
import { Section, Tiles, Tile, Badge, Table, Meter, Empty, Problem, fmtBytes, fmtMs } from './ui';
import { SettingsPanel } from './pages-settings';
import type { Snapshot } from './pages-core';

/**
 * Torrent streaming — the runtime picture, with its own settings at the bottom of the page.
 *
 * Ordered to answer three questions in sequence, because that is the order they have to be answered
 * in: can peers reach us, is the swarm healthy, and why is this stream slow. Reachability leads
 * because nearly every slow start traces back to it — a server nothing can dial has to chase every
 * connection outbound a few at a time, which reads as a healthy peer count and a stalling player.
 *
 * Configured and effective values are always shown as separate things. They diverge in exactly the
 * cases worth catching, and a dashboard that echoed the stored setting back would be reporting the
 * operator's intention to them and calling it status.
 */

interface Connectivity {
  configuredListenPort: number;
  actualListenPort: number;
  listenerBound: boolean;
  reachability: string;
  inboundConnections: number;
  outboundConnections: number;
  lastInboundAt: string | null;
  reportedEndPoint: string;
  portIsFixed: boolean;
  portForwardingRequested: boolean;
  mappingsCreated: string[];
  mappingsPending: string[];
  mappingsFailed: string[];
  dhtState: string;
  dhtEnabled: boolean;
  dhtNodes: number;
  dhtBytesSent: number;
  dhtBytesReceived: number;
  portForwarding: string;
  peerExchangeEnabled: boolean;
  localPeerDiscoveryEnabled: boolean;
  encryption: string[];
  maxConnections: number;
  maxHalfOpenConnections: number;
}

interface StreamRow {
  id: string;
  fileName: string;
  state: string;
  failureReason: string | null;
  kept: boolean;
  requiredBytesPerSecond: number;
  timeToReadyMs: number | null;
  timeToFirstFrameMs: number | null;
  deliveredBytes: number;
  stalls: number;
  worstStallMs: number;
  peers: number;
  openConnections: number;
  seeds: number;
  downloadRateBytesPerSecond: number;
  progress: number;
  torrentState: string;
  hasMetadata: boolean;
  diagnosis: string;
  recommendation: string;
  severity: string;
}

interface Check { name: string; passed: boolean; detail: string }

const list = (source: unknown, key: string): string[] => {
  const value = pick(source, key);
  return Array.isArray(value) ? value.map(String) : [];
};
const num = (source: unknown, key: string): number => Number(pick(source, key) ?? 0);
const bool = (source: unknown, key: string): boolean => Boolean(pick(source, key) ?? false);
const str = (source: unknown, key: string, fallback = ''): string => String(pick(source, key) ?? fallback);

function toConnectivity(raw: unknown): Connectivity | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  return {
    configuredListenPort: num(raw, 'ConfiguredListenPort'),
    actualListenPort: num(raw, 'ActualListenPort'),
    listenerBound: bool(raw, 'ListenerBound'),
    reachability: str(raw, 'Reachability', 'Unknown'),
    inboundConnections: num(raw, 'InboundConnections'),
    outboundConnections: num(raw, 'OutboundConnections'),
    lastInboundAt: (pick<string>(raw, 'LastInboundAt') ?? null) as string | null,
    reportedEndPoint: str(raw, 'ReportedEndPoint'),
    portIsFixed: bool(raw, 'PortIsFixed'),
    portForwardingRequested: bool(raw, 'PortForwardingRequested'),
    mappingsCreated: list(raw, 'MappingsCreated'),
    mappingsPending: list(raw, 'MappingsPending'),
    mappingsFailed: list(raw, 'MappingsFailed'),
    dhtState: str(raw, 'DhtState', '—'),
    dhtEnabled: bool(raw, 'DhtEnabled'),
    dhtNodes: num(raw, 'DhtNodes'),
    dhtBytesSent: num(raw, 'DhtBytesSent'),
    dhtBytesReceived: num(raw, 'DhtBytesReceived'),
    portForwarding: str(raw, 'PortForwarding', 'Off'),
    peerExchangeEnabled: bool(raw, 'PeerExchangeEnabled'),
    localPeerDiscoveryEnabled: bool(raw, 'LocalPeerDiscoveryEnabled'),
    encryption: list(raw, 'Encryption'),
    maxConnections: num(raw, 'MaxConnections'),
    maxHalfOpenConnections: num(raw, 'MaxHalfOpenConnections'),
  };
}

function toRow(raw: unknown): StreamRow {
  const ready = pick(raw, 'TimeToReadyMs');
  const first = pick(raw, 'TimeToFirstFrameMs');
  return {
    id: str(raw, 'Id'),
    fileName: str(raw, 'FileName'),
    state: str(raw, 'State', '?'),
    failureReason: (pick<string>(raw, 'FailureReason') ?? null) as string | null,
    kept: bool(raw, 'Kept'),
    requiredBytesPerSecond: num(raw, 'RequiredBytesPerSecond'),
    timeToReadyMs: ready === null || ready === undefined ? null : Number(ready),
    timeToFirstFrameMs: first === null || first === undefined ? null : Number(first),
    deliveredBytes: num(raw, 'DeliveredBytes'),
    stalls: num(raw, 'Stalls'),
    worstStallMs: num(raw, 'WorstStallMs'),
    peers: num(raw, 'Peers'),
    openConnections: num(raw, 'OpenConnections'),
    seeds: num(raw, 'Seeds'),
    downloadRateBytesPerSecond: num(raw, 'DownloadRateBytesPerSecond'),
    progress: num(raw, 'Progress'),
    torrentState: str(raw, 'TorrentState', '?'),
    hasMetadata: bool(raw, 'HasMetadata'),
    diagnosis: str(raw, 'Diagnosis'),
    recommendation: str(raw, 'Recommendation'),
    severity: str(raw, 'Severity', 'ok'),
  };
}

/** Sample of the connection ramp, kept client-side exactly as every other chart here is. */
interface RampPoint { at: number; open: number; delivered: number }

export function TorrentStreaming({ snap }: { snap: Snapshot | undefined }) {
  const { data, error } = usePoll<unknown>('OrcaEngine/Stream/Dashboard', 5000);
  const [ramp, setRamp] = useState<RampPoint[]>([]);
  const [probe, setProbe] = useState<{ verdict: string; summary: string; checks: Check[]; at: number }>();
  const [probing, setProbing] = useState(false);
  const seen = useRef<unknown>();

  // Sampled from the shared poll rather than retained server-side — the same trade the rest of the
  // Observatory makes: real history for as long as the page is open, at zero cost to the engine.
  useEffect(() => {
    if (!data || data === seen.current) return;
    seen.current = data;
    const rows = (pick<unknown[]>(data, 'Sessions') ?? []).map(toRow);
    if (rows.length === 0) return;
    setRamp((prev) => [...prev, {
      at: Date.now(),
      open: rows.reduce((n, r) => n + r.openConnections, 0),
      delivered: rows.reduce((n, r) => n + r.deliveredBytes, 0),
    }].slice(-120));
  }, [data]);

  // A probe result is a snapshot, and the tiles above it are live. Once the live verdict moves on,
  // the snapshot is not merely old but actively contradicts what is on screen beside it — observed
  // reporting "no inbound peer, 11 DHT nodes" while the tiles read Reachable with 30 peers and 35
  // nodes. Dropping it is better than explaining it.
  const liveVerdict = str(pick(data, 'Connectivity'), 'Reachability', '');
  useEffect(() => {
    if (probe && liveVerdict && probe.verdict !== liveVerdict) setProbe(undefined);
  }, [liveVerdict, probe]);

  if (error) return <Problem error={error} />;
  if (!data) return <Empty>Loading…</Empty>;

  const enabled = bool(data, 'Enabled');
  if (!enabled) {
    return (
      <Section title="Torrent streaming" subtitle="Currently switched off.">
        <Empty>
          Source streaming is disabled. Turn on <strong>Source streaming</strong> and set a Prowlarr
          URL and API key under Settings → Torrent streaming. Nothing here reports until then, because
          the engine holds no torrent session to report on.
        </Empty>
      </Section>
    );
  }

  const c = toConnectivity(pick(data, 'Connectivity'));
  const sessions = (pick<unknown[]>(data, 'Sessions') ?? []).map(toRow);
  const limits = pick(data, 'Limits');
  const cache = pick(data, 'Cache');
  const indexer = pick(data, 'Indexer');
  const problems = list(data, 'Problems');
  const counters = snap?.counters ?? {};

  const used = num(cache, 'UsedBytes');
  const budget = num(cache, 'BudgetBytes');
  const active = num(limits, 'ActiveSessions');
  const maxSessions = num(limits, 'MaxConcurrentStreamSessions');
  const checked = counters['swarm.verify.checked'] ?? 0;
  const inflated = counters['swarm.verify.inflated'] ?? 0;

  async function runProbe() {
    setProbing(true);
    try {
      const result = await get<unknown>('OrcaEngine/Stream/Reachability');
      setProbe({
        verdict: str(result, 'Verdict', 'Unknown'),
        summary: str(result, 'Summary'),
        checks: (pick<unknown[]>(result, 'Checks') ?? []).map((k) => ({
          name: str(k, 'Name'),
          passed: bool(k, 'Passed'),
          detail: str(k, 'Detail'),
        })),
        at: Date.now(),
      });
    } catch (e) {
      setProbe({ verdict: 'Unknown', summary: (e as Error).message, checks: [], at: Date.now() });
    } finally {
      setProbing(false);
    }
  }

  return (
    <>
      {problems.map((p) => <Problem key={p} error={p} />)}

      <Section
        title="Connectivity"
        subtitle="Whether peers can reach this server. Everything about how fast a stream starts follows from this."
        actions={
          <button className="obs-btn" onClick={() => void runProbe()} disabled={probing}>
            {probing ? 'Checking…' : 'Test inbound connectivity'}
          </button>
        }
      >
        {!c ? (
          <Empty>
            Peer discovery is not up. It normally starts with the server, so this means warm-up failed
            or has not run yet — check the log for “peer discovery warmed up”. It will also come up
            with the next stream.
          </Empty>
        ) : (
          <>
            <Tiles>
              <Tile
                label="Inbound reachability"
                value={reachLabel(c.reachability)}
                tone={reachTone(c.reachability)}
                hint={reachHint(c)}
              />
              <Tile
                label="Listening port"
                value={c.listenerBound ? c.actualListenPort : 'not bound'}
                tone={portTone(c)}
                hint={portHint(c)}
              />
              <Tile
                label="Port forwarding"
                value={forwardLabel(c.portForwarding)}
                tone={forwardTone(c.portForwarding)}
                hint={forwardHint(c)}
              />
              <Tile
                label="Peer connections"
                value={`${c.inboundConnections} in / ${c.outboundConnections} out`}
                hint="Inbound connections cost no outbound attempt — they are the free capacity."
              />
            </Tiles>

            <Tiles>
              <Tile
                label="DHT"
                value={c.dhtEnabled ? `${c.dhtState} · ${c.dhtNodes} nodes` : 'Disabled'}
                tone={!c.dhtEnabled ? undefined : c.dhtNodes > 0 ? 'ok' : 'warn'}
                hint={dhtHint(c)}
              />
              <Tile label="Peer exchange" value={<Badge on={c.peerExchangeEnabled}>{c.peerExchangeEnabled ? 'On' : 'Off'}</Badge>} />
              <Tile label="Local peer discovery" value={<Badge on={c.localPeerDiscoveryEnabled}>{c.localPeerDiscoveryEnabled ? 'On' : 'Off'}</Badge>} />
              <Tile label="Encryption" value={c.encryption.join(' → ') || '—'} hint="Offered to peers in this order." />
            </Tiles>

            {c.reportedEndPoint && (
              <p className="obs-muted obs-small">
                Advertising <strong>{c.reportedEndPoint}</strong> to trackers instead of the local
                endpoint above.
              </p>
            )}

            {probe && (
              <div className="obs-tablewrap">
                <Table head={['Check', 'Result', 'What was observed']}>
                  {probe.checks.map((k) => (
                    <tr key={k.name}>
                      <td>{k.name}</td>
                      <td><Badge on={k.passed}>{k.passed ? 'Yes' : 'No'}</Badge></td>
                      <td className="obs-muted obs-small">{k.detail}</td>
                    </tr>
                  ))}
                </Table>
                <p className={`obs-${reachTone(probe.verdict) ?? 'muted'}`}>
                  <strong>{reachLabel(probe.verdict)}</strong> — {probe.summary}
                </p>
                <p className="obs-muted obs-small">
                  Point-in-time check taken {new Date(probe.at).toLocaleTimeString()}. The tiles above
                  stay live.
                </p>
              </div>
            )}
          </>
        )}
      </Section>

      <Section title="Active sessions" subtitle={`${active} of ${maxSessions} slots in use.`}>
        {sessions.length === 0 ? (
          <Empty>No streams open.</Empty>
        ) : sessions.map((s) => (
          <div key={s.id} className="obs-section">
            <h3>{s.fileName || <span className="obs-muted">resolving file list…</span>} {s.kept && <Badge on>kept</Badge>}</h3>
            <p className={`obs-${s.severity}`}>
              <strong>{s.state}</strong> — {s.diagnosis}
            </p>
            {s.recommendation && <p className="obs-muted obs-small">{s.recommendation}</p>}
            {s.failureReason && <p className="obs-muted obs-small">{s.failureReason}</p>}

            <Tiles>
              <Tile label="Progress" value={`${s.progress.toFixed(1)}%`} hint={s.torrentState} />
              <Tile
                label="Open connections"
                value={c ? `${s.openConnections} / ${c.maxConnections}` : s.openConnections}
                tone={c && c.maxConnections > 0 && s.openConnections >= c.maxConnections * 0.9 ? 'warn' : undefined}
              />
              <Tile label="Peers" value={`${s.seeds} seed / ${s.peers} known`} />
              <Tile
                label="Swarm rate"
                value={`${fmtBytes(s.downloadRateBytesPerSecond)}/s`}
                tone={rateTone(s)}
                hint={s.requiredBytesPerSecond > 0
                  ? `Needs ${fmtBytes(s.requiredBytesPerSecond)}/s to sustain playback.`
                  : 'Required bitrate unknown — ffprobe could not read the container.'}
              />
            </Tiles>

            <Tiles>
              <Tile label="Time to ready" value={s.timeToReadyMs === null ? '—' : fmtMs(s.timeToReadyMs)} />
              <Tile label="Time to first frame" value={s.timeToFirstFrameMs === null ? 'not yet' : fmtMs(s.timeToFirstFrameMs)} />
              <Tile
                label="Stalls"
                value={s.stalls}
                tone={s.stalls > 0 ? 'warn' : undefined}
                hint={s.worstStallMs > 0 ? `Worst ${fmtMs(s.worstStallMs)}.` : undefined}
              />
              <Tile label="Delivered to player" value={fmtBytes(s.deliveredBytes)} />
            </Tiles>
          </div>
        ))}
      </Section>

      <Section
        title="Connection ramp"
        subtitle="Open connections over time. A line that climbs and then flattens against the ceiling is a limit; one that never climbs is a reachability problem."
      >
        {ramp.length < 2 ? (
          <Empty>Collecting samples — this fills in while a stream is open.</Empty>
        ) : (
          <Ramp points={ramp.map((p) => p.open)} ceiling={c?.maxConnections ?? 0} seconds={(ramp[ramp.length - 1].at - ramp[0].at) / 1000} />
        )}
      </Section>

      <Section title="Cache and capacity" subtitle="Pieces already on disk make a re-watch or a re-open instant.">
        <Meter value={used} max={budget} caption={`${fmtBytes(used)} of ${fmtBytes(budget)} — ${num(cache, 'Files')} files`} />
        <Tiles>
          <Tile label="Sessions" value={`${active} / ${maxSessions}`} tone={maxSessions > 0 && active >= maxSessions ? 'warn' : undefined} />
          <Tile label="Idle timeout" value={`${num(limits, 'StreamSessionIdleMinutes')} min`} hint="A session with no reads is torn down after this." />
          <Tile
            label="Open timeout"
            value={num(limits, 'StreamOpenTimeoutSeconds') > 0 ? `${num(limits, 'StreamOpenTimeoutSeconds')}s` : 'none'}
            hint="No limit means the viewer decides when to give up, not a constant."
          />
        </Tiles>
      </Section>

      <Section title="Indexers" subtitle="Where sources come from, and how truthful they turned out to be.">
        <Tiles>
          <Tile
            label="Prowlarr"
            value={<Badge on={bool(indexer, 'ProwlarrConfigured')}>{bool(indexer, 'ProwlarrConfigured') ? 'Configured' : 'Not set'}</Badge>}
            hint={`Searches cached ${num(indexer, 'SourceSearchCacheHours')}h.`}
          />
          <Tile label="Searches" value={counters['prowlarr.search.ok'] ?? 0} hint={`${counters['prowlarr.search.error'] ?? 0} failed.`} />
          <Tile
            label="Dropped as private"
            value={counters['prowlarr.search.dropped.private'] ?? 0}
            hint="Private-tracker results are never offered — streaming them is hit-and-run."
          />
          <Tile
            label="Inflated seeder counts"
            value={checked > 0 ? `${inflated} of ${checked}` : '—'}
            tone={checked > 0 && inflated / checked > 0.3 ? 'warn' : undefined}
            hint="Sources whose indexer claimed more seeders than the tracker actually reported."
          />
        </Tiles>
      </Section>

      <SettingsPanel tab="streaming" />
    </>
  );
}

/**
 * Connection ramp with its ceiling drawn in.
 *
 * Local rather than a shared primitive, and hand-drawn rather than a charting dependency: the one
 * thing this has to show is whether the line is climbing or pinned against the limit, and that needs
 * a reference line more than it needs a chart library.
 */
function Ramp({ points, ceiling, seconds }: { points: number[]; ceiling: number; seconds: number }) {
  const width = 560;
  const height = 120;
  const max = Math.max(ceiling || 0, ...points, 1);
  const step = width / Math.max(1, points.length - 1);
  const y = (v: number) => height - (v / max) * (height - 8) - 4;
  const path = points.map((p, i) => `${i * step},${y(p)}`).join(' ');

  return (
    <div>
      <svg width="100%" viewBox={`0 0 ${width} ${height}`} className="obs-spark" role="img"
        aria-label={`open connections, peak ${Math.max(...points)} of ${ceiling}`}>
        {ceiling > 0 && (
          <line x1="0" y1={y(ceiling)} x2={width} y2={y(ceiling)}
            stroke="currentColor" strokeWidth="1" strokeDasharray="4 4" opacity="0.45" />
        )}
        <polyline points={path} fill="none" stroke="currentColor" strokeWidth="2" />
      </svg>
      <p className="obs-muted obs-small">
        Peak {Math.max(...points)} open{ceiling > 0 && <> against a ceiling of {ceiling} (dashed)</>} over
        the last {Math.round(seconds)}s.
      </p>
    </div>
  );
}

function reachLabel(verdict: string): string {
  if (verdict === 'Reachable') return 'Reachable';
  if (verdict === 'NotReachable') return 'Not reachable';
  if (verdict === 'Pending') return 'Unproven';
  return 'Unknown';
}

function reachTone(verdict: string): 'ok' | 'warn' | 'bad' | undefined {
  if (verdict === 'Reachable') return 'ok';
  if (verdict === 'NotReachable') return 'bad';
  if (verdict === 'Pending') return 'warn';
  return undefined;
}

function reachHint(c: Connectivity): string {
  if (c.reachability === 'Reachable') {
    return `Proven by ${c.inboundConnections} inbound peer connection(s) — not inferred from the router.`;
  }
  if (c.reachability === 'NotReachable') return 'No listener is bound, so nothing can arrive.';
  if (c.reachability === 'Pending') {
    return 'Listening, but no peer has connected in yet. A router mapping alone does not prove peers can get through.';
  }
  return 'Not enough evidence either way.';
}

function portTone(c: Connectivity): 'ok' | 'warn' | 'bad' | undefined {
  if (!c.listenerBound) return 'bad';
  if (!c.portIsFixed) return 'warn';
  if (c.configuredListenPort > 0 && c.configuredListenPort !== c.actualListenPort) return 'bad';
  return 'ok';
}

function portHint(c: Connectivity): string {
  if (!c.listenerBound) return `Configured ${c.configuredListenPort || 'any'}, but nothing is bound — the port may be in use.`;
  if (c.configuredListenPort > 0 && c.configuredListenPort !== c.actualListenPort) {
    return `Configured ${c.configuredListenPort} but bound ${c.actualListenPort} — the setting did not take.`;
  }
  return c.portIsFixed
    ? 'Fixed, so a router forward for it stays valid across restarts.'
    : 'Random, and it changes every restart — so it cannot be forwarded. Set a fixed port in Settings.';
}

function forwardLabel(state: string): string {
  if (state === 'Working') return 'Working';
  if (state === 'Mapped') return 'Mapped, unproven';
  if (state === 'NotWorking') return 'Not working';
  return 'Off';
}

function forwardTone(state: string): 'ok' | 'warn' | 'bad' | undefined {
  if (state === 'Working') return 'ok';
  if (state === 'Mapped') return 'warn';
  if (state === 'NotWorking') return 'bad';
  return undefined;
}

function forwardHint(c: Connectivity): string {
  if (c.portForwarding === 'Working') {
    // Distinguish a UPnP mapping from a hand-forwarded port. Saying "mapped" when the router never
    // answered describes a mapping that does not exist, and would send someone looking for it.
    return c.mappingsCreated.length > 0
      ? `${c.mappingsCreated.join(', ')} — and ${c.inboundConnections} peer(s) arrived through it.`
      : `Forwarded outside UPnP — ${c.inboundConnections} peer(s) reached this server, which is the proof that matters.`;
  }
  if (c.portForwarding === 'Mapped') {
    // The single easiest wrong conclusion to draw on this page, so it is spelled out.
    return `${c.mappingsCreated.join(', ') || 'Negotiating'} — the router agreed, but nothing has come through yet, which CGNAT or an upstream router would look exactly like.`;
  }
  if (c.portForwarding === 'NotWorking') {
    return c.mappingsFailed.length > 0
      ? `Router refused: ${c.mappingsFailed.join(', ')}. Forward port ${c.actualListenPort || c.configuredListenPort} by hand instead.`
      : `No UPnP device answered. Forward port ${c.actualListenPort || c.configuredListenPort} by hand to gain inbound peers.`;
  }
  return 'UPnP is off. Forward the port manually to gain inbound peers.';
}

function dhtHint(c: Connectivity): string {
  if (!c.dhtEnabled) return 'Switched off — peers come from trackers only.';
  if (c.dhtNodes > 0) return `${fmtBytes(c.dhtBytesSent)} sent, ${fmtBytes(c.dhtBytesReceived)} received.`;
  // The distinction that matters: queries leaving with nothing coming back is a different fault from
  // DHT never having started, and only the sent count tells them apart.
  return c.dhtBytesSent > 0
    ? `${fmtBytes(c.dhtBytesSent)} of queries sent, ${fmtBytes(c.dhtBytesReceived)} received — traffic is leaving but no nodes are answering.`
    : 'No DHT traffic yet. It needs a few minutes after a restart to find its first nodes.';
}

function rateTone(s: StreamRow): 'ok' | 'warn' | 'bad' | undefined {
  if (s.requiredBytesPerSecond <= 0) return undefined;
  return s.downloadRateBytesPerSecond >= s.requiredBytesPerSecond ? 'ok' : 'bad';
}
