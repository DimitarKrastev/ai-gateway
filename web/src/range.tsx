import { useEffect, useMemo, useState, type ReactNode } from 'react'
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

  // Rolling presets follow the clock: re-anchor every 30 s so every page refreshes together.
  useEffect(() => {
    if (range.preset === 'custom') return
    const id = window.setInterval(() => setAnchor(Date.now()), 30_000)
    return () => window.clearInterval(id)
  }, [range.preset])

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
