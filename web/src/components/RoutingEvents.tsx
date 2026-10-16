import { Link } from 'react-router-dom'
import type { Account, RoutingEvent } from '../types'
import { fmtDateTime, shortId } from '../format'
import { Badge } from './ui'

const reasonText: Record<string, string> = {
  exhausted: 'account exhausted',
  unavailable: 'account unavailable',
  maximize: 'maximize utilization',
  reset_used: 'reset used',
}

export default function RoutingEvents({ events, accounts }: { events: RoutingEvent[]; accounts: Account[] | undefined }) {
  const name = (id: number | null) => (id === null ? '—' : (accounts?.find((a) => a.id === id)?.name ?? `#${id}`))
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Time</th>
            <th>Session</th>
            <th>From</th>
            <th>To</th>
            <th>Reason</th>
            <th>Detail</th>
          </tr>
        </thead>
        <tbody>
          {events.map((e) => (
            <tr key={e.id}>
              <td>{fmtDateTime(e.at)}</td>
              <td className="mono">
                {e.sessionId ? (
                  <Link to={`/sessions/${e.provider}/${encodeURIComponent(e.sessionId)}`} title={e.sessionId}>
                    {shortId(e.sessionId)}
                  </Link>
                ) : (
                  <span className="muted">—</span>
                )}
              </td>
              <td>{name(e.fromAccountId)}</td>
              <td>{name(e.toAccountId)}</td>
              <td>
                <Badge tone={e.reason === 'exhausted' ? 'warn' : e.reason === 'maximize' ? 'accent' : 'neutral'}>{reasonText[e.reason] ?? e.reason}</Badge>
              </td>
              <td className="muted small">{e.detail ?? ''}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
