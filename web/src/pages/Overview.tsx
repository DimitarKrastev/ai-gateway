import { Link } from 'react-router-dom'
import { qs } from '../api'
import { TimeChart } from '../components/charts'
import LiveThroughput from '../components/LiveThroughput'
import { metricDef, metricLabels } from '../metrics'
import { PageHead, RangePicker } from '../components/Layout'
import { AccountStatus, Async, Badge, Empty, Kpi, LimitBar, Panel } from '../components/ui'
import { fmtCompact, fmtInt, fmtMs, fmtPct, fmtUsd, kindLabel, shortId } from '../format'
import { useApi, useNow } from '../hooks'
import { useLive } from '../liveContext'
import { useRange } from '../rangeContext'
import type { Account, Live, Summary, Timeseries } from '../types'

export default function Overview() {
  const { from, to } = useRange()
  const range = { from, to }
  const summary = useApi<Summary>(`/api/analytics/summary${qs(range)}`)
  // In-flight requests are pushed by the gateway; fetch on load and after new usage (5-min counts), poll only offline.
  const pushed = useLive()
  const fetchedLive = useApi<Live>('/api/analytics/live', pushed.status === 'live' ? undefined : 3000, ['usage'])
  const usePushed = pushed.inFlight !== undefined && pushed.inFlight.receivedAt > fetchedLive.fetchedAt
  const live = usePushed ? { ...fetchedLive, data: pushed.inFlight!.data } : fetchedLive
  const liveAt = usePushed ? pushed.inFlight!.receivedAt : fetchedLive.fetchedAt
  const accounts = useApi<Account[]>('/api/accounts', 30_000, ['accounts', 'limits'])
  const cost = useApi<Timeseries>(`/api/analytics/timeseries${qs({ ...range, metric: 'cost', groupBy: 'provider' })}`)
  const tok = useApi<Timeseries>(
    `/api/analytics/timeseries${qs({ ...range, metric: 'inputTokens,outputTokens,cacheReadTokens,cacheWriteTokens', groupBy: 'none' })}`,
  )
  const conc = useApi<Timeseries>(`/api/analytics/timeseries${qs({ ...range, metric: 'concurrentAgents,concurrentRequests', groupBy: 'none' })}`)
  const now = useNow(1000)

  return (
    <>
      <PageHead title="Overview">
        <RangePicker />
      </PageHead>

      <LiveThroughput />

      <Async state={summary}>
        {(s) => (
          <div className="kpis">
            <Kpi label="Cost" value={fmtUsd(s.costUsd)} sub={s.unpricedRequests ? `${s.unpricedRequests} unpriced` : 'API-equivalent'} />
            <Kpi label="Requests" value={fmtInt(s.requests)} sub={`${fmtInt(s.sessions)} sessions · ${fmtInt(s.agents)} agents`} />
            <Kpi label="Input tokens" value={fmtCompact(s.inputTokens)} sub={`${fmtCompact(s.totalTokens)} total`} />
            <Kpi label="Output tokens" value={fmtCompact(s.outputTokens)} sub={s.reasoningTokens ? `${fmtCompact(s.reasoningTokens)} reasoning` : undefined} />
            <Kpi label="Cache read" value={fmtCompact(s.cacheReadTokens)} sub={`hit rate ${fmtPct(s.cacheHitRate === null ? null : s.cacheHitRate * 100)}`} />
            <Kpi label="Cache write" value={fmtCompact(s.cacheWriteTokens)} />
            <Kpi
              label="Active agents now"
              value={live.data ? fmtInt(live.data.activeAgents) : '—'}
              sub={`peak ${fmtInt(s.peakConcurrentAgents)} in range`}
            />
            <Kpi label="Tool calls" value={fmtCompact(s.toolCalls)} sub={`avg ctx ${fmtCompact(s.avgContextTokens)} · max ${fmtCompact(s.maxContextTokens)}`} />
            <Kpi
              label="Errors"
              value={fmtInt(s.errors)}
              tone={s.errors > 0 ? 'danger' : undefined}
              sub={`${fmtInt(s.rateLimited)} rate-limited · ${fmtInt(s.gatewayRetries)} failovers`}
            />
            <Kpi label="Avg latency" value={fmtMs(s.avgLatencyMs)} sub={`${fmtInt(s.models)} models`} />
          </div>
        )}
      </Async>

      <div className="grid-2">
        <Panel title="Cost over time" actions={<Link to="/analytics">Analytics →</Link>}>
          <Async state={cost}>{(d) => <TimeChart data={d} mode="stack" format={metricDef('cost').format} />}</Async>
        </Panel>
        <Panel title="Tokens over time">
          <Async state={tok}>{(d) => <TimeChart data={d} mode="stack" format={fmtCompact} labels={metricLabels(d)} />}</Async>
        </Panel>
      </div>

      <div className="grid-2">
        <Panel title="Account limits" actions={<Link to="/accounts">Manage →</Link>}>
          <Async state={accounts} empty={(a) => (a.length === 0 ? <Empty>No linked accounts yet. <Link to="/accounts">Link one</Link>.</Empty> : null)}>
            {(list) => (
              <div className="limit-summary">
                {list.map((a) => (
                  <div key={a.id} className="limit-summary-row">
                    <div className="limit-summary-head">
                      <strong>{a.name}</strong>
                      <span className="muted small">{kindLabel[a.kind]}</span>
                      <span className="spacer" />
                      {(a.limits?.resets.available ?? 0) > 0 && <Badge tone="accent">
                          {a.limits!.resets.available} reset{a.limits!.resets.available === 1 ? '' : 's'}
                        </Badge>}
                      <AccountStatus limits={a.limits} enabled={a.enabled} now={now} />
                    </div>
                    {a.limits && a.limits.windows.length > 0 ? (
                      <div className="limit-grid">
                        {a.limits.windows.map((w) => (
                          <LimitBar key={w.key} w={w} now={now} compact />
                        ))}
                      </div>
                    ) : (
                      <div className="muted small">No limit data yet — limits appear after the first request or refresh.</div>
                    )}
                  </div>
                ))}
              </div>
            )}
          </Async>
        </Panel>
        <Panel
          title={
            <>
              Live{' '}
              {live.data && (
                <span className="muted small">
                  {live.data.inFlightRequests} in flight · {live.data.activeSessions} sessions · {live.data.agentsLast5Min} agents in last 5 min
                </span>
              )}
            </>
          }
        >
          <Async state={live} empty={(l) => (l.requests.length === 0 ? <Empty>No requests in flight.</Empty> : null)}>
            {(l) => (
              <table className="table">
                <thead>
                  <tr>
                    <th>Provider</th>
                    <th>Account</th>
                    <th>Session</th>
                    <th>Agent</th>
                    <th>Model</th>
                    <th className="num">Elapsed</th>
                  </tr>
                </thead>
                <tbody>
                  {l.requests.map((r) => (
                    <tr key={r.requestId}>
                      <td>{r.provider}</td>
                      <td>{r.accountName ?? '—'}</td>
                      <td className="mono">{shortId(r.sessionId)}</td>
                      <td>{r.agentRole ?? '—'}</td>
                      <td>{r.model ?? '—'}</td>
                      <td className="num">{fmtMs(r.elapsedMs + Math.max(0, now - liveAt))}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </Async>
        </Panel>
      </div>

      <Panel title="Concurrency (peak per bucket)">
        <Async state={conc}>{(d) => <TimeChart data={d} mode="line" format={fmtCompact} labels={metricLabels(d)} height={200} />}</Async>
      </Panel>
    </>
  )
}
