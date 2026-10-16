import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useEffect, useState, type ReactNode } from 'react'
import { api } from './api'
import { LiveContext, MeterContext, type LiveCtx, type LiveTopic, type MeterCtx, type MeterSample } from './liveContext'
import type { Live } from './types'

const TOPICS: LiveTopic[] = ['usage', 'limits', 'accounts', 'routing']
const METER_SECONDS = 600

/** Merges samples by second, keeping the newest value per second and the last ten minutes. */
function mergeMeter(current: MeterSample[], incoming: MeterSample[]): MeterSample[] {
  const bySecond = new Map(current.map((s) => [s.t, s]))
  for (const s of incoming) bySecond.set(s.t, s)
  const newest = Math.max(0, ...bySecond.keys())
  return [...bySecond.values()].filter((s) => s.t > newest - METER_SECONDS * 1000).sort((a, b) => a.t - b.t)
}

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
  const [meter, setMeter] = useState<MeterCtx>({ samples: [], inFlight: 0, skew: 0 })

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
    // A second's sample is pushed just after the second ends, so its arrival dates the gateway clock.
    let skew: number | null = null
    // Each push: the second that just ended (last) plus earlier seconds revised by late corrections.
    connection.on('meter', (m: { samples: MeterSample[]; inFlight: number }) => {
      const latest = m.samples[m.samples.length - 1]
      if (latest) {
        const observed = Date.now() - (latest.t + 1000)
        skew = skew === null ? observed : skew * 0.8 + observed * 0.2
      }
      const sk = skew ?? 0
      setMeter((s) => ({ samples: mergeMeter(s.samples, m.samples), inFlight: m.inFlight, skew: sk }))
    })
    // History so the chart is full right away (and after a reconnect, the gap is filled).
    const loadMeter = () =>
      api<{ samples: MeterSample[]; inFlight: number }>(`/api/analytics/meter?seconds=${METER_SECONDS}`)
        .then((r) => setMeter((s) => ({ ...s, samples: mergeMeter(s.samples, r.samples), inFlight: r.inFlight })))
        .catch(() => {})
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
      loadMeter()
    })
    connection.onclose(() => setStatus('offline'))

    let stopped = false
    let retry: number | undefined
    const start = () => {
      connection
        .start()
        .then(() => {
          if (stopped) return
          setStatus('live')
          loadMeter()
        })
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

  return (
    <LiveContext.Provider value={state}>
      <MeterContext.Provider value={meter}>{children}</MeterContext.Provider>
    </LiveContext.Provider>
  )
}
