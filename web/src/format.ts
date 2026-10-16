import type { Time } from './types'

export function toMs(t: Time | undefined): number | null {
  if (t === null || t === undefined || t === '') return null
  if (typeof t === 'number') return t
  const ms = Date.parse(t)
  return Number.isNaN(ms) ? null : ms
}

export function fmtUsd(v: number | null | undefined): string {
  if (v === null || v === undefined) return '—'
  if (v === 0) return '$0.00'
  if (Math.abs(v) < 0.01) return `$${Number(v.toPrecision(2))}`
  if (Math.abs(v) < 1000) return `$${v.toFixed(2)}`
  return `$${Math.round(v).toLocaleString()}`
}

/** Compact count: 950, 12.3k, 4.56M, 1.2B. */
export function fmtCompact(v: number | null | undefined): string {
  if (v === null || v === undefined) return '—'
  const a = Math.abs(v)
  if (a < 1000) return Number.isInteger(v) || a >= 100 ? String(Math.round(v)) : v.toFixed(1)
  if (a < 1e6) return `${(v / 1e3).toFixed(a < 1e4 ? 2 : 1)}k`
  if (a < 1e9) return `${(v / 1e6).toFixed(a < 1e7 ? 2 : 1)}M`
  return `${(v / 1e9).toFixed(2)}B`
}

export const fmtTokens = fmtCompact

export function fmtInt(v: number | null | undefined): string {
  return v === null || v === undefined ? '—' : Math.round(v).toLocaleString()
}

export function fmtPct(v: number | null | undefined, digits = 0): string {
  return v === null || v === undefined ? '—' : `${v.toFixed(digits)}%`
}

export function fmtMs(v: number | null | undefined): string {
  if (v === null || v === undefined) return '—'
  if (v < 1000) return `${Math.round(v)} ms`
  if (v < 60_000) return `${(v / 1000).toFixed(1)} s`
  return fmtDuration(v)
}

/** Output speed: "84 tok/s", "1.2k tok/s". */
export function fmtTps(v: number | null | undefined): string {
  if (v === null || v === undefined) return '—'
  return `${v >= 1000 ? fmtCompact(v) : v >= 100 ? Math.round(v) : v.toFixed(1)} tok/s`
}

export function fmtDuration(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000))
  const d = Math.floor(s / 86400)
  const h = Math.floor((s % 86400) / 3600)
  const m = Math.floor((s % 3600) / 60)
  if (d > 0) return `${d}d ${h}h`
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m ${s % 60}s`
  return `${s}s`
}

/** "in 2h 5m" / "3m 10s ago". */
export function fmtRelative(t: Time | undefined, now = Date.now()): string {
  const ms = toMs(t)
  if (ms === null) return '—'
  const diff = ms - now
  if (Math.abs(diff) < 5000) return 'now'
  return diff > 0 ? `in ${fmtDuration(diff)}` : `${fmtDuration(-diff)} ago`
}

export function fmtDateTime(t: Time | undefined): string {
  const ms = toMs(t)
  if (ms === null) return '—'
  const d = new Date(ms)
  const sameDay = new Date().toDateString() === d.toDateString()
  return sameDay
    ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
    : d.toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

export function fmtAxisTime(ms: number, bucketMs: number): string {
  const d = new Date(ms)
  if (bucketMs >= 86_400_000) return d.toLocaleDateString([], { month: 'short', day: 'numeric' })
  if (bucketMs >= 3_600_000 * 6)
    return d.toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit' })
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

export function shortId(id: string | null | undefined, n = 8): string {
  if (!id) return '—'
  return id.length > n + 2 ? `${id.slice(0, n)}…` : id
}

export const kindLabel: Record<string, string> = {
  anthropic_api_key: 'Anthropic API key',
  anthropic_oauth: 'Claude subscription',
  openai_api_key: 'OpenAI API key',
  chatgpt_oauth: 'ChatGPT subscription',
}

export const providerLabel: Record<string, string> = { anthropic: 'Anthropic', openai: 'OpenAI' }

export function statusTone(s: string): 'ok' | 'warn' | 'danger' {
  return s === 'exhausted' ? 'danger' : s === 'warning' ? 'warn' : 'ok'
}
