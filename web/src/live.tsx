import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useEffect, useState, type ReactNode } from 'react'
import { LiveContext, type LiveCtx, type LiveTopic } from './liveContext'
import type { Live } from './types'

const TOPICS: LiveTopic[] = ['usage', 'limits', 'accounts', 'routing']

/**
 * One SignalR connection per signed-in tab (`/api/live`, cookie auth). The gateway pushes the in-flight snapshot and
 * "changed" notices; pages refetch through `useApi(..., topics)` and the range re-anchors on new usage.
 */
export function LiveProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<LiveCtx>({
    status: 'connecting',
    versions: { usage: 0, limits: 0, accounts: 0, routing: 0 },
    inFlight: undefined,
  })

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/api/live')
      // Keep retrying for as long as the tab is open (the gateway may be restarted).
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (c) => Math.min(30_000, 1000 * 2 ** Math.min(c.previousRetryCount, 5)) })
      .configureLogging(LogLevel.Warning)
      .build()

    const setStatus = (status: LiveCtx['status']) => setState((s) => (s.status === status ? s : { ...s, status }))
    const bumpAll = () =>
      setState((s) => ({ ...s, versions: Object.fromEntries(TOPICS.map((t) => [t, s.versions[t] + 1])) as LiveCtx['versions'] }))

    connection.on('inFlight', (data: Live) => setState((s) => ({ ...s, inFlight: { data, receivedAt: Date.now() } })))
    connection.on('changed', (msg: { topics: string[] }) =>
      setState((s) => {
        const versions = { ...s.versions }
        for (const t of msg.topics) if ((TOPICS as string[]).includes(t)) versions[t as LiveTopic]++
        return { ...s, versions }
      }),
    )
    connection.onreconnecting(() => setStatus('reconnecting'))
    // Changes during the outage were missed: refetch everything.
    connection.onreconnected(() => {
      setStatus('live')
      bumpAll()
    })
    connection.onclose(() => setStatus('offline'))

    let stopped = false
    let retry: number | undefined
    const start = () => {
      connection
        .start()
        .then(() => !stopped && setStatus('live'))
        .catch(() => {
          if (stopped) return
          setStatus('offline')
          retry = window.setTimeout(start, 5000)
        })
    }
    start()
    return () => {
      stopped = true
      window.clearTimeout(retry)
      if (connection.state !== HubConnectionState.Disconnected) void connection.stop()
    }
  }, [])

  return <LiveContext.Provider value={state}>{children}</LiveContext.Provider>
}
