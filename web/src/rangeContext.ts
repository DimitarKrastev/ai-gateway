import { createContext, useContext } from 'react'

export type RangePreset = '1h' | '24h' | '7d' | '30d' | 'custom'
export type RangeState = { preset: RangePreset; customFrom: string; customTo: string }

export const presetMs: Record<Exclude<RangePreset, 'custom'>, number> = {
  '1h': 3_600_000,
  '24h': 86_400_000,
  '7d': 7 * 86_400_000,
  '30d': 30 * 86_400_000,
}

export type Ctx = {
  range: RangeState
  setRange: (r: RangeState) => void
  /** Resolved window; `to` follows a refresh anchor so query strings stay stable between renders. */
  from: number
  to: number
  refresh: () => void
}

export const RangeContext = createContext<Ctx | null>(null)

export function useRange() {
  const ctx = useContext(RangeContext)
  if (!ctx) throw new Error('useRange must be used inside RangeProvider')
  return ctx
}
