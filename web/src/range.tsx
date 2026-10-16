import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useLive } from './liveContext'
import { presetMs, RangeContext, type Ctx, type RangeState } from './rangeContext'

const STORAGE_KEY = 'aigw.range'

function load(): RangeState {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (raw) return JSON.parse(raw) as RangeState
  } catch {
    /* ignore */
  }
  return { preset: '24h', customFrom: '', customTo: '' }
}

export function RangeProvider({ children }: { children: ReactNode }) {
  const [range, setRangeState] = useState<RangeState>(load)
  const [anchor, setAnchor] = useState(() => Date.now())

  // A window that ends "now" (rolling presets, or custom with no end) follows the clock and new traffic.
  const followsNow = range.preset !== 'custom' || !range.customTo
  // Slide with the clock even when nothing happens; short windows slide in smaller steps.
  const tickMs = range.preset === '5m' || range.preset === '15m' ? 5_000 : range.preset === '1h' ? 15_000 : 30_000
  useEffect(() => {
    if (!followsNow) return
    const id = window.setInterval(() => setAnchor(Date.now()), tickMs)
    return () => window.clearInterval(id)
  }, [followsNow, tickMs])

  // Live: re-anchor when the gateway records a request, so every page refetches with it in range. At most once a
  // second (a burst of subagent traffic is one refetch), and the last change is never dropped.
  const { versions } = useLive()
  const lastAnchor = useRef(0)
  useEffect(() => {
    if (!followsNow || versions.usage === 0) return
    const wait = Math.max(0, lastAnchor.current + 1000 - Date.now())
    const id = window.setTimeout(() => {
      lastAnchor.current = Date.now()
      setAnchor(Date.now())
    }, wait)
    return () => window.clearTimeout(id)
  }, [versions.usage, followsNow])

  const value = useMemo<Ctx>(() => {
    let from: number
    let to: number
    if (range.preset === 'custom') {
      to = range.customTo ? Date.parse(range.customTo) : anchor
      from = range.customFrom ? Date.parse(range.customFrom) : to - presetMs['24h']
      if (Number.isNaN(to)) to = anchor
      if (Number.isNaN(from) || from >= to) from = to - presetMs['24h']
    } else {
      to = anchor
      from = to - presetMs[range.preset]
    }
    return {
      range,
      from,
      to,
      setRange: (r) => {
        setRangeState(r)
        setAnchor(Date.now())
        try {
          localStorage.setItem(STORAGE_KEY, JSON.stringify(r))
        } catch {
          /* ignore */
        }
      },
      refresh: () => setAnchor(Date.now()),
    }
  }, [range, anchor])

  return <RangeContext.Provider value={value}>{children}</RangeContext.Provider>
}
