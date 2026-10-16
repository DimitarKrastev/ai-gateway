import { fmtCompact, fmtMs, fmtUsd } from './format'
import type { Timeseries } from './types'

export const PALETTE = ['#4c6ef5', '#12b886', '#f59f00', '#e64980', '#7950f2', '#15aabf', '#fa5252', '#82c91e', '#868e96']

export type MetricDef = { key: string; label: string; kind: 'sum' | 'avg' | 'peak'; format: (v: number) => string }

const tokens = (v: number) => fmtCompact(v)
const count = (v: number) => fmtCompact(v)

export const METRICS: MetricDef[] = [
  { key: 'cost', label: 'Cost (USD)', kind: 'sum', format: fmtUsd },
  { key: 'requests', label: 'Requests', kind: 'sum', format: count },
  { key: 'inputTokens', label: 'Input tokens', kind: 'sum', format: tokens },
  { key: 'outputTokens', label: 'Output tokens', kind: 'sum', format: tokens },
  { key: 'cacheReadTokens', label: 'Cache read tokens', kind: 'sum', format: tokens },
  { key: 'cacheWriteTokens', label: 'Cache write tokens', kind: 'sum', format: tokens },
  { key: 'reasoningTokens', label: 'Reasoning tokens', kind: 'sum', format: tokens },
  { key: 'totalTokens', label: 'Total tokens', kind: 'sum', format: tokens },
  { key: 'contextAvg', label: 'Context size (avg)', kind: 'avg', format: tokens },
  { key: 'contextMax', label: 'Context size (max)', kind: 'peak', format: tokens },
  { key: 'toolCalls', label: 'Tool calls', kind: 'sum', format: count },
  { key: 'errors', label: 'Errors', kind: 'sum', format: count },
  { key: 'latencyAvg', label: 'Latency (avg)', kind: 'avg', format: fmtMs },
  { key: 'ttfbAvg', label: 'Time to first byte (avg)', kind: 'avg', format: fmtMs },
  { key: 'concurrentAgents', label: 'Concurrent agents (peak)', kind: 'peak', format: count },
  { key: 'concurrentRequests', label: 'Concurrent requests (peak)', kind: 'peak', format: count },
  { key: 'activeAgents', label: 'Active agents', kind: 'peak', format: count },
  { key: 'activeSessions', label: 'Active sessions', kind: 'peak', format: count },
]

export const metricDef = (key: string) => METRICS.find((m) => m.key === key) ?? METRICS[1]


/** Series labels for multi-metric charts: series id is the metric name. */
export function metricLabels(ts: Timeseries | undefined): Record<string, string> {
  const out: Record<string, string> = {}
  for (const s of ts?.series ?? []) {
    const def = METRICS.find((m) => m.key === (s.id ?? s.label))
    if (def) out[s.key] = def.label
  }
  return out
}
