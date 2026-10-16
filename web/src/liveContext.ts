import { createContext, useContext } from 'react'
import type { Live } from './types'

/** What the gateway says changed; views that show it refetch. */
export type LiveTopic = 'usage' | 'limits' | 'accounts' | 'routing'

export type LiveStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

export type LiveCtx = {
  status: LiveStatus
  /** Bumped each time the gateway reports a change to the topic. */
  versions: Record<LiveTopic, number>
  /** In-flight snapshot pushed by the gateway, with the time it arrived. */
  inFlight: { data: Live; receivedAt: number } | undefined
}

export const LiveContext = createContext<LiveCtx | null>(null)

const OFFLINE: LiveCtx = { status: 'offline', versions: { usage: 0, limits: 0, accounts: 0, routing: 0 }, inFlight: undefined }

/** Outside a LiveProvider (e.g. the login page) everything behaves as if offline: plain fetching and polling. */
export function useLive(): LiveCtx {
  return useContext(LiveContext) ?? OFFLINE
}
