import { useState } from 'react'
import { qs } from '../api'
import BreakdownTable from '../components/BreakdownTable'
import { TimeChart } from '../components/charts'
import { PageHead, RangePicker } from '../components/Layout'
import { Async, Empty, Kpi, Panel } from '../components/ui'
import { fmtCompact, fmtInt, fmtUsd } from '../format'
import { useApi } from '../hooks'
import { metricDef, METRICS } from '../metrics'
import { useRange } from '../rangeContext'
import type { Account, BreakdownRow, Summary, Timeseries } from '../types'

const GROUPS = [
  { key: 'none', label: 'None' },
  { key: 'account', label: 'Account' },
  { key: 'session', label: 'Session' },
  { key: 'agent', label: 'Agent type' },
  { key: 'agentId', label: 'Agent' },
  { key: 'model', label: 'Model' },
  { key: 'provider', label: 'Provider' },
  { key: 'client', label: 'Client' },
]
const BREAKDOWNS = ['account', 'session', 'agent', 'agentId', 'model', 'provider', 'client', 'tool']
const BUCKETS = ['auto', 'minute', '5min', '15min', 'hour', '6hour', 'day']

type Filters = { provider: string; accountId: string; model: string; agent: string; sessionId: string; agentId: string }
const NO_FILTERS: Filters = { provider: '', accountId: '', model: '', agent: '', sessionId: '', agentId: '' }

const breakdownLabel = (b: string) => GROUPS.find((g) => g.key === b)?.label ?? (b === 'tool' ? 'Tool' : b)

export default function Analytics() {
  const { from, to } = useRange()
  const [metric, setMetric] = useState('cost')
  const [groupBy, setGroupBy] = useState('model')
  const [bucket, setBucket] = useState('auto')
  const [chartMode, setChartMode] = useState<'auto' | 'stack' | 'line'>('auto')
  const [by, setBy] = useState('model')
  const [f, setF] = useState<Filters>(NO_FILTERS)

  const base = { from, to, ...f }
  const def = metricDef(metric)
  const mode = chartMode === 'auto' ? (def.kind === 'sum' ? 'stack' : 'line') : chartMode

  const accounts = useApi<Account[]>('/api/accounts')
  const models = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ from, to, by: 'model', limit: 100 })}`)
  const agents = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ from, to, by: 'agent', limit: 100 })}`)
  const summary = useApi<Summary>(`/api/analytics/summary${qs(base)}`)
  const ts = useApi<Timeseries>(`/api/analytics/timeseries${qs({ ...base, metric, groupBy, bucket, limit: 8 })}`)
  const breakdown = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ ...base, by, limit: 100 })}`)

  const set = (k: keyof Filters, v: string) => setF((p) => ({ ...p, [k]: v }))
  const active = Object.values(f).some(Boolean)

  const pick = (row: BreakdownRow) => {
    const map: Record<string, keyof Filters> = {
      account: 'accountId',
      session: 'sessionId',
      agent: 'agent',
      agentId: 'agentId',
      model: 'model',
      provider: 'provider',
    }
    const k = map[by]
    if (k) set(k, row.key)
  }

  return (
    <>
      <PageHead title="Analytics">
        <RangePicker />
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
            <label>
              Bucket
              <select value={bucket} onChange={(e) => setBucket(e.target.value)}>
                {BUCKETS.map((b) => (
                  <option key={b} value={b}>
                    {b}
                  </option>
                ))}
              </select>
            </label>
            <div className="seg" role="group" aria-label="Chart type">
              {(['auto', 'stack', 'line'] as const).map((m) => (
                <button key={m} className={chartMode === m ? 'on' : ''} onClick={() => setChartMode(m)}>
                  {m === 'auto' ? 'Auto' : m === 'stack' ? 'Stacked' : 'Lines'}
                </button>
              ))}
            </div>
          </div>
        }
      >
        <Async state={ts}>{(d) => <TimeChart data={d} mode={mode} format={def.format} height={320} />}</Async>
      </Panel>

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
