import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { qs } from '../api'
import BreakdownTable from '../components/BreakdownTable'
import { TimeChart } from '../components/charts'
import { PageHead } from '../components/Layout'
import RequestsTable from '../components/RequestsTable'
import RoutingEvents from '../components/RoutingEvents'
import { Async, Empty, Kpi, Panel } from '../components/ui'
import { fmtCompact, fmtDateTime, fmtDuration, fmtInt, fmtUsd, toMs } from '../format'
import { useApi } from '../hooks'
import { metricLabels } from '../metrics'
import type { Account, BreakdownRow, RequestRow, RoutingEvent, Summary, Timeseries } from '../types'

export default function SessionDetail() {
  const { provider = '', sessionId = '' } = useParams()
  // A session's whole lifetime: from the epoch to a little past now.
  const [to] = useState(() => Date.now() + 60_000)
  const filter = useMemo(() => ({ provider, sessionId, from: 0, to }), [provider, sessionId, to])

  const summary = useApi<Summary>(`/api/analytics/summary${qs(filter)}`, 10_000)
  // Charts span the session's own lifetime (bucketing from the epoch would be meaningless).
  const first = toMs(summary.data?.firstSeen)
  const last = toMs(summary.data?.lastSeen)
  const span =
    first !== null && last !== null
      ? { provider, sessionId, from: first - 30_000, to: Math.max(last + 30_000, first + 5 * 60_000) }
      : null
  const tokens = useApi<Timeseries>(
    span && `/api/analytics/timeseries${qs({ ...span, metric: 'inputTokens,outputTokens,cacheReadTokens,cacheWriteTokens' })}`,
    10_000,
  )
  const context = useApi<Timeseries>(span && `/api/analytics/timeseries${qs({ ...span, metric: 'contextMax', groupBy: 'agentId' })}`, 10_000)
  const agents = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ ...filter, by: 'agentId' })}`, 10_000)
  const tools = useApi<{ rows: BreakdownRow[] }>(`/api/analytics/breakdown${qs({ ...filter, by: 'tool' })}`, 10_000)
  const requests = useApi<{ items: RequestRow[] }>(`/api/analytics/requests${qs({ ...filter, limit: 200, includeRetried: true })}`, 10_000)
  const events = useApi<RoutingEvent[]>(`/api/routing/events${qs({ sessionId, limit: 100 })}`)
  const accounts = useApi<Account[]>('/api/accounts')

  return (
    <>
      <PageHead title="Session">
        <Link to="/sessions">← All sessions</Link>
      </PageHead>
      <div className="mono session-id">
        {provider} · {sessionId}
      </div>

      <Async state={summary}>
        {(s) => {
          const first = toMs(s.firstSeen)
          const last = toMs(s.lastSeen)
          return (
            <div className="kpis compact">
              <Kpi label="Cost" value={fmtUsd(s.costUsd)} />
              <Kpi label="Requests" value={fmtInt(s.requests)} sub={s.errors ? `${s.errors} errors` : undefined} />
              <Kpi label="Agents" value={fmtInt(s.agents)} sub={`peak ${s.peakConcurrentAgents} concurrent`} />
              <Kpi label="Input" value={fmtCompact(s.inputTokens)} />
              <Kpi label="Output" value={fmtCompact(s.outputTokens)} />
              <Kpi label="Cache read" value={fmtCompact(s.cacheReadTokens)} />
              <Kpi label="Cache write" value={fmtCompact(s.cacheWriteTokens)} />
              <Kpi label="Max context" value={fmtCompact(s.maxContextTokens)} />
              <Kpi label="Tool calls" value={fmtInt(s.toolCalls)} />
              <Kpi
                label="Duration"
                value={first && last ? fmtDuration(last - first) : '—'}
                sub={first ? `started ${fmtDateTime(first)}` : undefined}
              />
            </div>
          )
        }}
      </Async>

      <div className="grid-2">
        <Panel title="Tokens over time">
          <Async state={tokens} empty={() => (span ? null : <Empty>No requests recorded.</Empty>)}>{(d) => <TimeChart data={d} mode="stack" format={fmtCompact} labels={metricLabels(d)} height={220} />}</Async>
        </Panel>
        <Panel title="Context size by agent">
          <Async state={context} empty={() => (span ? null : <Empty>No requests recorded.</Empty>)}>{(d) => <TimeChart data={d} mode="line" format={fmtCompact} height={220} />}</Async>
        </Panel>
      </div>

      <div className="grid-2">
        <Panel title="Agents">
          <Async state={agents} empty={(d) => (d.rows.length === 0 ? <Empty>No agent activity.</Empty> : null)}>
            {(d) => <BreakdownTable by="agentId" rows={d.rows} />}
          </Async>
        </Panel>
        <Panel title="Tools">
          <Async state={tools} empty={(d) => (d.rows.length === 0 ? <Empty>No tool calls.</Empty> : null)}>
            {(d) => <BreakdownTable by="tool" rows={d.rows} />}
          </Async>
        </Panel>
      </div>

      <Panel title="Account moves">
        <Async state={events} empty={(e) => (e.length === 0 ? <Empty>This session has stayed on one account.</Empty> : null)}>
          {(e) => <RoutingEvents events={e} accounts={accounts.data} />}
        </Async>
      </Panel>

      <Panel title="Requests">
        <Async state={requests} empty={(d) => (d.items.length === 0 ? <Empty>No requests.</Empty> : null)}>
          {(d) => <RequestsTable rows={d.items} showSession={false} />}
        </Async>
      </Panel>
    </>
  )
}
