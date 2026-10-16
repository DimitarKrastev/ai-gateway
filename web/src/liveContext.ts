import { createContext, useContext } from 'react'
import type { Live } from './types'

/** What the gateway says changed; views that show it refetch. */
export type LiveTopic = 'usage' | 'limits' | 'accounts' | 'routing'

export type LiveStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

/** One second of gateway traffic: tokens generated and prompt tokens sent. `t` is the second's start (ms). */
export type MeterSample = { t: number; output: number; prompt: number }

export type LiveCtx = {
  status: LiveStatus
  /** Bumped each time the gateway reports a change to the topic. */
  versions: Record<LiveTopic, number>
  /** In-flight snapshot pushed by the gateway, with the time it arrived. */
  inFlight: { data: Live; receivedAt: number } | undefined
}

/** Per-second throughput, in its own context so the once-a-second updates re-render only the throughput chart. */
export type MeterCtx = {
  /** The last ten minutes, oldest first (history fetched on connect, then one push a second). */
  samples: MeterSample[]
  /** Requests in flight as of the latest sample. */
  inFlight: number
  /** Local clock minus gateway clock (ms), estimated from when each second's sample arrives. */
  skew: number
}

export const MeterContext = createContext<MeterCtx>({ samples: [], inFlight: 0, skew: 0 })

export function useMeter(): MeterCtx {
  return useContext(MeterContext)
}

export const LiveContext = createContext<LiveCtx | null>(null)

const OFFLINE: LiveCtx = {
  status: 'offline',
  versions: { usage: 0, limits: 0, accounts: 0, routing: 0 },
  inFlight: undefined,
}

/** Outside a LiveProvider (e.g. the login page) everything behaves as if offline: plain fetching and polling. */
export function useLive(): LiveCtx {
  return useContext(LiveContext) ?? OFFLINE
}
