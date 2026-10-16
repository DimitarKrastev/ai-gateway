import { useEffect, useState } from 'react'
import { api, qs } from '../api'
import { PageHead } from '../components/Layout'
import RequestsTable from '../components/RequestsTable'
import { Empty, ErrorBox, Loading, Panel, Toggle } from '../components/ui'
import type { Account, RequestRow } from '../types'
import { useApi } from '../hooks'

type Page = { items: RequestRow[]; nextBefore: number | null }
type LogFilter = { provider: string; accountId: string; includeRetried: boolean }

// The request log ignores the global range: it pages back through all history.
const logUrl = (f: LogFilter, before?: number) =>
  `/api/analytics/requests${qs({ ...f, from: 0, to: Date.now() + 60_000, limit: 50, before })}`

export default function Requests() {
  const [provider, setProvider] = useState('')
  const [accountId, setAccountId] = useState('')
  const [includeRetried, setIncludeRetried] = useState(true)
  const [rows, setRows] = useState<RequestRow[] | null>(null)
  const [next, setNext] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loadingMore, setLoadingMore] = useState(false)
  const [tick, setTick] = useState(0)
  const accounts = useApi<Account[]>('/api/accounts', undefined, ['accounts'])

  useEffect(() => {
    let cancelled = false
    api<Page>(logUrl({ provider, accountId, includeRetried }))
      .then((p) => {
        if (cancelled) return
        setRows(p.items)
        setNext(p.nextBefore)
        setError(null)
      })
      .catch((e: Error) => !cancelled && setError(e.message))
    return () => {
      cancelled = true
    }
  }, [provider, accountId, includeRetried, tick])

  async function more() {
    if (next === null) return
    setLoadingMore(true)
    try {
      const p = await api<Page>(logUrl({ provider, accountId, includeRetried }, next))
      setRows((r) => [...(r ?? []), ...p.items])
      setNext(p.nextBefore)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoadingMore(false)
    }
  }

  return (
    <>
      <PageHead title="Request log">
        <select aria-label="Provider" value={provider} onChange={(e) => setProvider(e.target.value)}>
          <option value="">All providers</option>
          <option value="anthropic">Anthropic</option>
          <option value="openai">OpenAI</option>
        </select>
        <select aria-label="Account" value={accountId} onChange={(e) => setAccountId(e.target.value)}>
          <option value="">All accounts</option>
          {accounts.data?.map((a) => (
            <option key={a.id} value={String(a.id)}>
              {a.name}
            </option>
          ))}
        </select>
        <Toggle checked={includeRetried} onChange={setIncludeRetried} label="Show failed-over attempts" />
        <button className="secondary small" onClick={() => setTick((t) => t + 1)}>
          ↻ Refresh
        </button>
      </PageHead>
      <Panel>
        {error && <ErrorBox error={error} onRetry={() => setTick((t) => t + 1)} />}
        {rows === null ? (
          !error && <Loading />
        ) : rows.length === 0 ? (
          <Empty>No requests yet. Point Claude Code or Codex at the gateway (see Settings → Client keys).</Empty>
        ) : (
          <>
            <RequestsTable rows={rows} />
            <div className="pager">
              <span className="muted small">{rows.length} shown</span>
              <span className="spacer" />
              {next !== null && (
                <button className="secondary small" onClick={more} disabled={loadingMore}>
                  {loadingMore ? 'Loading…' : 'Load older'}
                </button>
              )}
            </div>
          </>
        )}
      </Panel>
    </>
  )
}
