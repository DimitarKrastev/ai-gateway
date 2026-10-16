import { useMemo, useState } from 'react'
import { qs } from '../api'
import BreakdownTable from '../components/BreakdownTable'
import { AgentChart, ModeComparisonChart, TimeChart, type TpsStat } from '../components/charts'
import { PageHead, RangePicker } from '../components/Layout'
import LiveChart from '../components/LiveChart'
import { Async, Empty, Kpi, Panel } from '../components/ui'
import { fmtCompact, fmtInt, fmtTps, fmtUsd } from '../format'
import { useApi, useNow } from '../hooks'
import type { LiveTopic } from '../liveContext'
import { LIVE_WINDOWS, type LiveSeries, type LiveWindowKey } from '../liveWindows'
import { metricDef, METRICS, PALETTE } from '../metrics'
import { useRange } from '../rangeContext'
import type { Account, AgentSeriesResult, BreakdownRow, Summary, Timeseries } from '../types'

const GROUPS = [
  { key: 'none', label: 'None' },
  { key: 'account', label: 'Account' },
  { key: 'session', label: 'Session' },
  { key: 'agent', label: 'Agent type' },
  { key: 'agentId', label: 'Agent (each instance)' },
  { key: 'model', label: 'Model' },
  { key: 'requestedModel', label: 'Requested model' },
  { key: 'speedMode', label: 'Speed mode' },
  { key: 'modelMode', label: 'Model + mode' },
  { key: 'provider', label: 'Provider' },
  { key: 'client', label: 'Client' },
]
const BREAKDOWNS = ['account', 'session', 'agent', 'agentId', 'model', 'requestedModel', 'speedMode', 'modelMode', 'provider', 'client', 'tool']
const BUCKETS = ['auto', '5s', '15s', '30s', 'minute', '5min', '15min', 'hour', '6hour', 'day']
const TPS_STATS: Array<{ key: TpsStat; label: string }> = [
  { key: 'tokensPerSecond', label: 'Weighted avg' },
  { key: 'tokensPerSecondP50', label: 'p50' },
  { key: 'tokensPerSecondP95', label: 'p95' },
]

type Filters = {
  provider: string
  accountId: string
  model: string
  requestedModel: string
  speedMode: string
  agent: string
  sessionId: string
  agentId: string
}
const NO_FILTERS: Filters = { provider: '', accountId: '', model: '', requestedModel: '', speedMode: '', agent: '', sessionId: '', agentId: '' }

const breakdownLabel = (b: string) =>
  b === 'agentId' ? 'Agent' : (GROUPS.find((g) => g.key === b)?.label ?? (b === 'tool' ? 'Tool' : b))

type LiveState = { on: boolean; window: LiveWindowKey }
const LIVE_KEY = 'aigw.analyticsLive'

function loadLive(): LiveState {
  try {
    const v = JSON.parse(localStorage.getItem(LIVE_KEY) ?? 'null') as LiveState | null
    if (v && LIVE_WINDOWS.some((w) => w.key === v.window)) return { on: !!v.on, window: v.window }
  } catch {
    /* ignore */
  }
  return { on: false, window: '5m' }
}

const seriesColor = (label: string, i: number) => (label.startsWith('Other') ? PALETTE[8] : PALETTE[i % 8])

function liveFromTimeseries(d: Timeseries): LiveSeries[] {
  return d.series.map((s, i) => ({
    key: s.key,
    label: s.label,
    color: seriesColor(s.label, i),
    points: d.points.flatMap((p) => (typeof p[s.key] === 'number' ? [[p.t, p[s.key] as number] as [number, number]] : [])),
  }))
}

function liveFromAgents(d: AgentSeriesResult): LiveSeries[] {
  return d.agents.map((a, i) => ({ key: a.key, label: a.label, color: PALETTE[i % 8], points: a.points }))
}

export default function Analytics() {
  const range = useRange()
  const [live, setLiveState] = useState<LiveState>(loadLive)
  const setLive = (v: LiveState) => {
    setLiveState(v)
    try {
      localStorage.setItem(LIVE_KEY, JSON.stringify(v))
    } catch {
      /* ignore */
    }
  }
  const liveMs = LIVE_WINDOWS.find((w) => w.key === live.window)!.ms
  // Live: a window ending now, re-anchored every 10 s (ending 20 s ahead, so data recorded in between is included when a
  // usage push triggers a refetch); the charts scroll on their own in between.
  const anchor = Math.floor(useNow(10_000) / 10_000) * 10_000
  const { from, to } = live.on ? { from: anchor - liveMs - 10_000, to: anchor + 20_000 } : range
  const lt = live.on ? (['usage'] as LiveTopic[]) : undefined
  // Live views place a request when it completes (its cost and tokens are known then), so long requests show up now.
  const timeBasis = live.on ? 'completed' : undefined
  const [metric, setMetric] = useState('cost')
  const [groupBy, setGroupBy] = useState('model')
  const [bucket, setBucket] = useState('auto')
  const [chartMode, setChartMode] = useState<'auto' | 'stack' | 'line'>('auto')
  const [cumulative, setCumulative] = useState(false)
  const [by, setBy] = useState('model')
  const [tpsStat, setTpsStat] = useState<TpsStat>('tokensPerSecond')
  const [f, setF] = useState<Filters>(NO_FILTERS)

  const base = { from, to, timeBasis, ...f }
  const def = metricDef(metric)
  const mode = chartMode === 'auto' ? (def.kind === 'sum' ? 'stack' : 'line') : chartMode
  // "Agent (each instance)" plots every request of every agent; metrics without a per-request value fall back to buckets.
  const perAgent = groupBy === 'agentId' && !!def.perRequest

  const accounts = useApi<Account[]>('/api/accounts', undefined, ['accounts'])
  const bucketQ = live.on ? '1s' : bucket
  const models = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ from, to, timeBasis, by: 'model', limit: 100 })}`, undefined, lt)
  const agents = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ from, to, timeBasis, by: 'agent', limit: 100 })}`, undefined, lt)
  const modes = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ from, to, timeBasis, by: 'speedMode', limit: 20 })}`, undefined, lt)
  const summary = useApi<Summary>(`/api/analytics/summary${qs(base)}`, undefined, lt)
  const ts = useApi<Timeseries>(
    perAgent ? null : `/api/analytics/timeseries${qs({ ...base, metric, groupBy, bucket: bucketQ, limit: 8 })}`,
    undefined,
    lt,
  )
  const agentSeries = useApi<AgentSeriesResult>(
    perAgent ? `/api/analytics/agent-series${qs({ ...base, metric, cumulative: def.kind === 'sum' && cumulative, limit: 12 })}` : null,
    undefined,
    lt,
  )
  const breakdown = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ ...base, by, limit: 100 })}`, undefined, lt)
  const tpsOverTime = useApi<Timeseries>(
    `/api/analytics/timeseries${qs({ ...base, metric: tpsStat, groupBy: 'modelMode', bucket: bucketQ, limit: 8 })}`,
    undefined,
    lt,
  )
  const tpsByModelMode = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ ...base, by: 'modelMode', limit: 100 })}`, undefined, lt)
  const mainLive = useMemo(
    () => (perAgent ? (agentSeries.data ? liveFromAgents(agentSeries.data) : []) : ts.data ? liveFromTimeseries(ts.data) : []),
    [perAgent, agentSeries.data, ts.data],
  )
  const tpsLive = useMemo(() => (tpsOverTime.data ? liveFromTimeseries(tpsOverTime.data) : []), [tpsOverTime.data])

  const set = (k: keyof Filters, v: string) => setF((p) => ({ ...p, [k]: v }))
  const active = Object.values(f).some(Boolean)

  const pick = (row: BreakdownRow) => {
    const map: Record<string, keyof Filters> = {
      account: 'accountId',
      session: 'sessionId',
      agent: 'agent',
      agentId: 'agentId',
      model: 'model',
      requestedModel: 'requestedModel',
      speedMode: 'speedMode',
      provider: 'provider',
    }
    if (by === 'modelMode') {
      const cut = row.key.lastIndexOf('|')
      setF((p) => ({ ...p, model: row.key.slice(0, cut), speedMode: row.key.slice(cut + 1) }))
      return
    }
    const k = map[by]
    if (k) set(k, row.key)
  }

  return (
    <>
      <PageHead title="Analytics">
        <label className="live-toggle" title="Charts scroll in real time at one-second resolution over a rolling window">
          <input type="checkbox" checked={live.on} onChange={(e) => setLive({ ...live, on: e.target.checked })} />
          Live
        </label>
        {live.on ? (
          <div className="seg" role="group" aria-label="Live window">
            {LIVE_WINDOWS.map((w) => (
              <button key={w.key} className={live.window === w.key ? 'on' : ''} onClick={() => setLive({ ...live, window: w.key })}>
                Last {w.key}
              </button>
            ))}
          </div>
        ) : (
          <RangePicker />
        )}
      </PageHead>

      <div className="filters">
        <label>
          Provider
          <select value={f.provider} onChange={(e) => set('provider', e.target.value)}>
            <option value="">All</option>
            <option value="anthropic">Anthropic</option>
            <option value="openai">OpenAI</option>
          </select>
        </label>
        <label>
          Account
          <select value={f.accountId} onChange={(e) => set('accountId', e.target.value)}>
            <option value="">All</option>
            {accounts.data?.map((a) => (
              <option key={a.id} value={String(a.id)}>
                {a.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Model
          <select value={f.model} onChange={(e) => set('model', e.target.value)}>
            <option value="">All</option>
            {models.data?.rows.map((r) => (
              <option key={r.key} value={r.key}>
                {r.label || r.key}
              </option>
            ))}
            {f.model && !models.data?.rows.some((r) => r.key === f.model) && <option value={f.model}>{f.model}</option>}
          </select>
        </label>
        <label>
          Speed mode
          <select value={f.speedMode} onChange={(e) => set('speedMode', e.target.value)}>
            <option value="">All</option>
            {modes.data?.rows.map((r) => (
              <option key={r.key} value={r.key}>
                {r.label || r.key}
              </option>
            ))}
            {f.speedMode && !modes.data?.rows.some((r) => r.key === f.speedMode) && <option value={f.speedMode}>{f.speedMode}</option>}
          </select>
        </label>
        <label>
          Agent type
          <select value={f.agent} onChange={(e) => set('agent', e.target.value)}>
            <option value="">All</option>
            {agents.data?.rows.map((r) => (
              <option key={r.key} value={r.key}>
                {r.label || r.key}
              </option>
            ))}
            {f.agent && !agents.data?.rows.some((r) => r.key === f.agent) && <option value={f.agent}>{f.agent}</option>}
          </select>
        </label>
        <label>
          Session
          <input placeholder="session id" value={f.sessionId} onChange={(e) => set('sessionId', e.target.value.trim())} />
        </label>
        {f.agentId && (
          <label>
            Agent
            <input value={f.agentId} onChange={(e) => set('agentId', e.target.value.trim())} />
          </label>
        )}
        {f.requestedModel && (
          <label>
            Requested model
            <input value={f.requestedModel} onChange={(e) => set('requestedModel', e.target.value.trim())} />
          </label>
        )}
        {active && (
          <button className="link" onClick={() => setF(NO_FILTERS)}>
            Clear filters
          </button>
        )}
      </div>

      <Async state={summary}>
        {(s) => (
          <div className="kpis compact">
            <Kpi label="Cost" value={fmtUsd(s.costUsd)} />
            <Kpi label="Requests" value={fmtInt(s.requests)} />
            <Kpi label="Input" value={fmtCompact(s.inputTokens)} />
            <Kpi label="Output" value={fmtCompact(s.outputTokens)} />
            <Kpi label="Cache read" value={fmtCompact(s.cacheReadTokens)} />
            <Kpi label="Cache write" value={fmtCompact(s.cacheWriteTokens)} />
            <Kpi label="Tool calls" value={fmtInt(s.toolCalls)} />
            <Kpi
              label="Output speed"
              value={fmtTps(s.tokensPerSecond)}
              sub={s.throughputRequests ? `p50 ${fmtCompact(s.tokensPerSecondP50)} · p95 ${fmtCompact(s.tokensPerSecondP95)}` : 'no measured requests'}
            />
            <Kpi label="Sessions" value={fmtInt(s.sessions)} />
            <Kpi label="Agents" value={fmtInt(s.agents)} sub={`peak ${fmtInt(s.peakConcurrentAgents)} concurrent`} />
            <Kpi label="Errors" value={fmtInt(s.errors)} tone={s.errors ? 'danger' : undefined} />
          </div>
        )}
      </Async>

      <Panel
        title={def.label}
        actions={
          <div className="toolbar">
            <label>
              Metric
              <select value={metric} onChange={(e) => setMetric(e.target.value)}>
                {METRICS.map((m) => (
                  <option key={m.key} value={m.key}>
                    {m.label}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Group by
              <select value={groupBy} onChange={(e) => setGroupBy(e.target.value)}>
                {GROUPS.map((g) => (
                  <option key={g.key} value={g.key}>
                    {g.label}
                  </option>
                ))}
              </select>
            </label>
            {perAgent ? (
              def.kind === 'sum' && (
                <div className="seg" role="group" aria-label="Value per request or running total">
                  <button className={!cumulative ? 'on' : ''} onClick={() => setCumulative(false)}>
                    Per request
                  </button>
                  <button className={cumulative ? 'on' : ''} onClick={() => setCumulative(true)}>
                    Running total
                  </button>
                </div>
              )
            ) : (
              <>
                {!live.on && <label>
                  Bucket
                  <select value={bucket} onChange={(e) => setBucket(e.target.value)}>
                    {BUCKETS.map((b) => (
                      <option key={b} value={b}>
                        {b}
                      </option>
                    ))}
                  </select>
                </label>}
                <div className="seg" role="group" aria-label="Chart type">
                  {(['auto', 'stack', 'line'] as const).map((m) => (
                    <button key={m} className={chartMode === m ? 'on' : ''} onClick={() => setChartMode(m)}>
                      {m === 'auto' ? 'Auto' : m === 'stack' ? 'Stacked' : 'Lines'}
                    </button>
                  ))}
                </div>
              </>
            )}
          </div>
        }
      >
        {live.on ? (
          <LiveChart
            series={mainLive}
            bucketMs={perAgent ? null : 1000}
            mode={perAgent ? 'line' : mode}
            windowMs={liveMs}
            format={def.format}
            axisFormat={def.format === fmtTps ? fmtCompact : def.format}
            height={320}
          />
        ) : perAgent ? (
          <Async state={agentSeries}>
            {(d) => (
              <AgentChart data={d} format={def.format} axisFormat={def.format === fmtTps ? fmtCompact : def.format} height={320} sessionScoped={!!f.sessionId} />
            )}
          </Async>
        ) : (
          <Async state={ts}>{(d) => (
              <TimeChart data={d} mode={mode} format={def.format} axisFormat={def.format === fmtTps ? fmtCompact : def.format} height={320} />
            )}</Async>
        )}
      </Panel>

      <div className="grid-2">
        <Panel
          title="Output speed over time"
          actions={
            <div className="seg" role="group" aria-label="Output speed statistic">
              {TPS_STATS.map((s) => (
                <button key={s.key} className={tpsStat === s.key ? 'on' : ''} onClick={() => setTpsStat(s.key)}>
                  {s.label}
                </button>
              ))}
            </div>
          }
        >
          {live.on ? (
            <LiveChart series={tpsLive} bucketMs={1000} mode="line" windowMs={liveMs} format={fmtTps} axisFormat={fmtCompact} height={260} />
          ) : (
            <Async state={tpsOverTime}>{(d) => <TimeChart data={d} mode="line" format={fmtTps} axisFormat={fmtCompact} height={260} />}</Async>
          )}
        </Panel>
        <Panel title={`Output speed by model and mode (${TPS_STATS.find((s) => s.key === tpsStat)?.label})`}>
          <Async state={tpsByModelMode}>{(d) => <ModeComparisonChart rows={d.rows} stat={tpsStat} format={fmtTps} />}</Async>
          <p className="muted small chart-note">
            Streaming: output tokens ÷ time from the first token to the end. Non-streaming responses use the whole latency, so
            they read slower.
          </p>
        </Panel>
      </div>

      <Panel
        title="Breakdown"
        actions={
          <div className="seg" role="tablist" aria-label="Breakdown dimension">
            {BREAKDOWNS.map((b) => (
              <button key={b} role="tab" aria-selected={by === b} className={by === b ? 'on' : ''} onClick={() => setBy(b)}>
                {breakdownLabel(b)}
              </button>
            ))}
          </div>
        }
      >
        <Async state={breakdown} empty={(d) => (d.rows.length === 0 ? <Empty>No requests match.</Empty> : null)}>
          {(d) => <BreakdownTable by={by} rows={d.rows} onPick={by === 'tool' || by === 'client' ? undefined : pick} />}
        </Async>
      </Panel>
    </>
  )
}
