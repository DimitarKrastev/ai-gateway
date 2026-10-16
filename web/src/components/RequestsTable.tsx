import { Link } from 'react-router-dom'
import type { RequestRow } from '../types'
import { fmtCompact, fmtDateTime, fmtMs, fmtUsd, shortId } from '../format'
import { Badge } from './ui'

function StatusCell({ r }: { r: RequestRow }) {
  if (r.status === null) return <Badge tone="accent">in flight</Badge>
  const tone = r.status >= 500 || r.status === 429 ? 'danger' : r.status >= 400 ? 'warn' : 'ok'
  return (
    <span title={[r.errorType, r.stopReason, r.cancelled ? 'cancelled by client' : null].filter(Boolean).join(' · ')}>
      <Badge tone={tone}>{r.status}</Badge>
      {r.retried && <span className="muted small"> retried</span>}
      {r.cancelled && <span className="muted small"> cancelled</span>}
    </span>
  )
}

export default function RequestsTable({ rows, showSession = true }: { rows: RequestRow[]; showSession?: boolean }) {
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Time</th>
            <th>Status</th>
            <th>Provider</th>
            <th>Account</th>
            {showSession && <th>Session</th>}
            <th>Agent</th>
            <th>Model</th>
            <th>Endpoint</th>
            <th className="num">Input</th>
            <th className="num">Output</th>
            <th className="num">Cache R</th>
            <th className="num">Cache W</th>
            <th className="num">Context</th>
            <th className="num">Tools</th>
            <th className="num">Cost</th>
            <th className="num">Latency</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.id} className={r.retried ? 'dim' : ''}>
              <td title={fmtDateTime(r.startedAt)}>{fmtDateTime(r.startedAt)}</td>
              <td>
                <StatusCell r={r} />
              </td>
              <td>{r.provider}</td>
              <td>{r.accountName ?? '—'}</td>
              {showSession && (
                <td className="mono">
                  {r.sessionId ? (
                    <Link to={`/sessions/${r.provider}/${encodeURIComponent(r.sessionId)}`} title={r.sessionId}>
                      {shortId(r.sessionId)}
                    </Link>
                  ) : (
                    '—'
                  )}
                </td>
              )}
              <td title={r.agentId ?? ''}>
                {r.agentRole ?? '—'}
                {r.client && <span className="muted small"> · {r.client}</span>}
              </td>
              <td>{r.model ?? '—'}</td>
              <td>
                {r.endpoint}
                {r.stream && <span className="muted small"> · stream</span>}
              </td>
              <td className="num">{fmtCompact(r.inputTokens)}</td>
              <td className="num">{fmtCompact(r.outputTokens)}</td>
              <td className="num">{fmtCompact(r.cacheReadTokens)}</td>
              <td className="num">{fmtCompact(r.cacheWriteTokens)}</td>
              <td className="num">{fmtCompact(r.contextTokens)}</td>
              <td
                className="num"
                title={r.tools ? Object.entries(r.tools).map(([k, v]) => `${k} ×${v}`).join(', ') : ''}
              >
                {r.toolCalls || ''}
              </td>
              <td className="num">{fmtUsd(r.costUsd)}</td>
              <td className="num" title={r.ttfbMs !== null ? `first byte ${fmtMs(r.ttfbMs)}` : ''}>
                {fmtMs(r.latencyMs)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
