import type { SpeedRow } from '../types'
import { fmtCompact, fmtInt, fmtMs, fmtTps } from '../format'

const tps = (v: number | null) => (v === null ? '—' : fmtTps(v).replace(' tok/s', ''))

/** Output speed per provider · model · speed mode, measured end to end (request sent → response received). */
export default function SpeedTable({ rows, onPick }: { rows: SpeedRow[]; onPick?: (row: SpeedRow) => void }) {
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Provider</th>
            <th>Model</th>
            <th>Mode</th>
            <th className="num" title="Successful requests with output that were measured">
              Requests
            </th>
            <th className="num">Output</th>
            <th className="num" title="Total output tokens ÷ total time from sending the request to receiving the whole response">
              Tok/s end to end
            </th>
            <th className="num">p50</th>
            <th className="num">p95</th>
            <th className="num" title="Average time from sending the request to the first output token">
              Time to first token
            </th>
            <th className="num">Avg duration</th>
            <th className="num" title="Output tokens per second after the first token (streaming only), for comparison">
              Tok/s generating
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.key} className={onPick ? 'clickable' : ''} onClick={onPick ? () => onPick(r) : undefined} title={onPick ? 'Filter by this' : undefined}>
              <td>{r.provider}</td>
              <td>{r.model}</td>
              <td>
                <span className={`badge ${r.mode === 'fast' ? 'accent' : ''}`}>{r.mode}</span>
              </td>
              <td className="num">{fmtInt(r.requests)}</td>
              <td className="num">{fmtCompact(r.outputTokens)}</td>
              <td className="num strong">{tps(r.endToEnd)}</td>
              <td className="num">{tps(r.endToEndP50)}</td>
              <td className="num">{tps(r.endToEndP95)}</td>
              <td className="num">{r.avgTtftMs === null ? '—' : fmtMs(r.avgTtftMs)}</td>
              <td className="num">{fmtMs(r.avgDurationMs)}</td>
              <td className="num muted">{tps(r.generation)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
