import type { BreakdownRow } from '../types'
import { fmtCompact, fmtDateTime, fmtInt, fmtMs, fmtUsd } from '../format'

export default function BreakdownTable({
  by,
  rows,
  onPick,
}: {
  by: string
  rows: BreakdownRow[]
  /** Clicking a row applies it as a filter (when supported for this dimension). */
  onPick?: (row: BreakdownRow) => void
}) {
  if (by === 'tool')
    return (
      <div className="table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Tool</th>
              <th className="num">Calls</th>
              <th className="num">Requests</th>
              <th className="num">Sessions</th>
              <th className="num">Agents</th>
              <th>Last used</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.key}>
                <td className="mono">{r.label}</td>
                <td className="num">{fmtInt(r.toolCalls)}</td>
                <td className="num">{fmtInt(r.requests)}</td>
                <td className="num">{fmtInt(r.sessions)}</td>
                <td className="num">{fmtInt(r.agents)}</td>
                <td>{fmtDateTime(r.lastSeen)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    )

  const totalCost = rows.reduce((a, r) => a + (r.costUsd ?? 0), 0)
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>{by[0].toUpperCase() + by.slice(1)}</th>
            <th className="num">Requests</th>
            <th className="num">Errors</th>
            <th className="num">Cost</th>
            <th className="num">Share</th>
            <th className="num">Input</th>
            <th className="num">Output</th>
            <th className="num">Cache read</th>
            <th className="num">Cache write</th>
            <th className="num">Tool calls</th>
            <th className="num">Avg ctx</th>
            <th className="num">Max ctx</th>
            <th className="num">Avg latency</th>
            <th className="num">Sessions</th>
            <th className="num">Agents</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.key} className={onPick ? 'clickable' : ''} onClick={onPick ? () => onPick(r) : undefined} title={onPick ? 'Filter by this' : undefined}>
              <td className={by === 'session' || by === 'agentId' ? 'mono' : ''}>{r.label || r.key}</td>
              <td className="num">{fmtInt(r.requests)}</td>
              <td className={`num ${r.errors ? 'text-danger' : ''}`}>{fmtInt(r.errors)}</td>
              <td className="num">{fmtUsd(r.costUsd)}</td>
              <td className="num">
                <div className="share">
                  <div style={{ width: `${totalCost ? (r.costUsd / totalCost) * 100 : 0}%` }} />
                </div>
              </td>
              <td className="num">{fmtCompact(r.inputTokens)}</td>
              <td className="num">{fmtCompact(r.outputTokens)}</td>
              <td className="num">{fmtCompact(r.cacheReadTokens)}</td>
              <td className="num">{fmtCompact(r.cacheWriteTokens)}</td>
              <td className="num">{fmtInt(r.toolCalls)}</td>
              <td className="num">{fmtCompact(r.avgContextTokens)}</td>
              <td className="num">{fmtCompact(r.maxContextTokens)}</td>
              <td className="num">{fmtMs(r.avgLatencyMs)}</td>
              <td className="num">{fmtInt(r.sessions)}</td>
              <td className="num">{fmtInt(r.agents)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
