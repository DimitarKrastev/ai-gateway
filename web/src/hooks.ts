import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import { useLive, type LiveTopic } from './liveContext'

export type ApiState<T> = {
  data: T | undefined
  error: string | null
  loading: boolean
  reload: () => void
  /** When the shown data was fetched (ms), 0 before the first response. */
  fetchedAt: number
}

/** Fetches `path` (null = skip), refetching when it changes, when the gateway reports a change to one of `topics`,
 * and optionally polling every `pollMs`. Previous data stays visible while a new path loads. */
export function useApi<T>(path: string | null, pollMs?: number, topics?: LiveTopic[]): ApiState<T> {
  const [tick, setTick] = useState(0)
  const reload = useCallback(() => setTick((t) => t + 1), [])
  // Live updates: a subscribed topic's version is part of the fetch key, so a reported change refetches.
  const { versions } = useLive()
  const versionKey = topics?.map((t) => versions[t]).join(',') ?? ''
  const key = path === null ? null : `${path}#${tick}#${versionKey}`
  const [result, setResult] = useState<{ key: string; data: T | undefined; error: string | null; at: number }>()

  useEffect(() => {
    if (key === null || path === null) return
    let cancelled = false
    api<T>(path)
      .then((data) => !cancelled && setResult({ key, data, error: null, at: Date.now() }))
      .catch((e: Error) => !cancelled && setResult((r) => ({ key, data: r?.data, error: e.message, at: r?.at ?? 0 })))
    return () => {
      cancelled = true
    }
  }, [key, path])

  useEffect(() => {
    if (!pollMs || path === null) return
    const id = window.setInterval(reload, pollMs)
    return () => window.clearInterval(id)
  }, [pollMs, path, reload])

  return {
    data: result?.data,
    error: result?.error ?? null,
    loading: key !== null && result?.key !== key,
    reload,
    fetchedAt: result?.at ?? 0,
  }
}

/** Re-renders every `ms` so relative times ("in 3m") stay current. */
export function useNow(ms = 1000): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), ms)
    return () => window.clearInterval(id)
  }, [ms])
  return now
}
