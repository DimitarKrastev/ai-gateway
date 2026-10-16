import { useState } from 'react'
import { Link } from 'react-router-dom'
import { qs } from '../api'
import { PageHead, RangePicker } from '../components/Layout'
import RoutingEvents from '../components/RoutingEvents'
import { Async, Badge, Empty, Panel } from '../components/ui'
import { fmtCompact, fmtDateTime, fmtInt, fmtRelative, fmtUsd, shortId } from '../format'
import { useApi } from '../hooks'
import { useRange } from '../rangeContext'
import type { Account, RoutingEvent, SessionRow } from '../types'

const PAGE = 50

export default function Sessions() {
  const { from, to } = useRange()
  const [offset, setOffset] = useState(0)
  const [provider, setProvider] = useState('')
  const sessions = useApi<{ total: number; items: SessionRow[] }>(`/api/analytics/sessions${qs({ from, to, provider, limit: PAGE, offset })}`)
  const events = useApi<RoutingEvent[]>('/api/routing/events?limit=50', undefined, ['routing'])
  const accounts = useApi<Account[]>('/api/accounts', undefined, ['accounts'])

  return (
    <>
      <PageHead title="Sessions">
        <select aria-label="Provider" value={provider} onChange={(e) => (setProvider(e.target.value), setOffset(0))}>
          <option value="">All providers</option>
          <option value="anthropic">Anthropic</option>
          <option value="openai">OpenAI</option>
        </select>
        <RangePicker />
      </PageHead>
      <Panel>
        <Async state={sessions} empty={(d) => (d.items.length === 0 ? <Empty>No sessions in this range.</Empty> : null)}>
          {(d) => (
            <>
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Session</th>
                      <th>Provider</th>
                      <th>Client</th>
                      <th>Account</th>
                      <th>Model</th>
                      <th className="num">Agents</th>
                      <th className="num">Requests</th>
                      <th className="num">Input</th>
                      <th className="num">Output</th>
                      <th className="num">Cache R</th>
                      <th className="num">Cache W</th>
                      <th className="num">Max ctx</th>
                      <th className="num">Tools</th>
                      <th className="num">Cost</th>
                      <th>First seen</th>
                      <th>Last seen</th>
                    </tr>
                  </thead>
                  <tbody>
                    {d.items.map((s) => (
                      <tr key={`${s.provider}:${s.sessionId}`}>
                        <td className="mono">
                          <Link to={`/sessions/${s.provider}/${encodeURIComponent(s.sessionId)}`} title={s.sessionId}>
                            {shortId(s.sessionId, 12)}
                          </Link>
                          {s.live && (
                            <>
                              {' '}
                              <Badge tone="accent">live</Badge>
                            </>
                          )}
                        </td>
                        <td>{s.provider}</td>
                        <td>{s.client ?? '—'}</td>
                        <td>
                          {s.accountName ?? '—'}
                          {s.accountsUsed > 1 && <span className="muted small"> (+{s.accountsUsed - 1} moved)</span>}
                        </td>
                        <td>{s.model ?? '—'}</td>
                        <td className="num">{fmtInt(s.agents)}</td>
                        <td className="num">
                          {fmtInt(s.requests)}
                          {s.errors > 0 && <span className="text-danger small"> ({s.errors} err)</span>}
                        </td>
                        <td className="num">{fmtCompact(s.inputTokens)}</td>
                        <td className="num">{fmtCompact(s.outputTokens)}</td>
                        <td className="num">{fmtCompact(s.cacheReadTokens)}</td>
                        <td className="num">{fmtCompact(s.cacheWriteTokens)}</td>
                        <td className="num">{fmtCompact(s.maxContextTokens)}</td>
                        <td className="num">{fmtInt(s.toolCalls)}</td>
                        <td className="num">{fmtUsd(s.costUsd)}</td>
                        <td>{fmtDateTime(s.firstSeen)}</td>
                        <td title={fmtDateTime(s.lastSeen)}>{fmtRelative(s.lastSeen)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="pager">
                <span className="muted small">
                  {offset + 1}–{offset + d.items.length} of {d.total}
                </span>
                <span className="spacer" />
                <button className="secondary small" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
                  ← Newer
                </button>
                <button className="secondary small" disabled={offset + PAGE >= d.total} onClick={() => setOffset(offset + PAGE)}>
                  Older →
                </button>
              </div>
            </>
          )}
        </Async>
      </Panel>
      <Panel title="Session moves (routing events)">
        <Async state={events} empty={(e) => (e.length === 0 ? <Empty>No sessions have been moved between accounts.</Empty> : null)}>
          {(e) => <RoutingEvents events={e} accounts={accounts.data} />}
        </Async>
      </Panel>
    </>
  )
}
