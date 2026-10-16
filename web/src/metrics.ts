import { fmtCompact, fmtMs, fmtTps, fmtUsd } from './format'
import type { Timeseries } from './types'

export const PALETTE = ['#4c6ef5', '#12b886', '#f59f00', '#e64980', '#7950f2', '#15aabf', '#fa5252', '#82c91e', '#868e96']

export type MetricDef = {
  key: string
  label: string
  kind: 'sum' | 'avg' | 'peak'
  format: (v: number) => string
  /** Has a value per request, so it can be plotted per agent instance (/api/analytics/agent-series). */
  perRequest?: boolean
}

const tokens = (v: number) => fmtCompact(v)
const count = (v: number) => fmtCompact(v)

export const METRICS: MetricDef[] = [
  { key: 'cost', label: 'Cost (USD)', kind: 'sum', format: fmtUsd, perRequest: true },
  { key: 'requests', label: 'Requests', kind: 'sum', format: count, perRequest: true },
  { key: 'inputTokens', label: 'Input tokens', kind: 'sum', format: tokens, perRequest: true },
  { key: 'outputTokens', label: 'Output tokens', kind: 'sum', format: tokens, perRequest: true },
  { key: 'cacheReadTokens', label: 'Cache read tokens', kind: 'sum', format: tokens, perRequest: true },
  { key: 'cacheWriteTokens', label: 'Cache write tokens', kind: 'sum', format: tokens, perRequest: true },
  { key: 'reasoningTokens', label: 'Reasoning tokens', kind: 'sum', format: tokens, perRequest: true },
  { key: 'totalTokens', label: 'Total tokens', kind: 'sum', format: tokens, perRequest: true },
  { key: 'contextAvg', label: 'Context size (avg)', kind: 'avg', format: tokens, perRequest: true },
  { key: 'contextMax', label: 'Context size (max)', kind: 'peak', format: tokens, perRequest: true },
  // Each agent holds its latest request's context until its next request (or 60 s after its last response); groups sum
  // their agents, so stacked areas show the context in use at every moment.
  { key: 'contextWindow', label: 'Context window (in use)', kind: 'sum', format: tokens },
  { key: 'toolCalls', label: 'Tool calls', kind: 'sum', format: count, perRequest: true },
  { key: 'errors', label: 'Errors', kind: 'sum', format: count, perRequest: true },
  { key: 'latencyAvg', label: 'Latency (avg)', kind: 'avg', format: fmtMs, perRequest: true },
  { key: 'ttfbAvg', label: 'Time to first byte (avg)', kind: 'avg', format: fmtMs, perRequest: true },
  { key: 'tokensPerSecond', label: 'Output tokens/s (weighted avg)', kind: 'avg', format: fmtTps, perRequest: true },
  { key: 'tokensPerSecondP50', label: 'Output tokens/s (p50)', kind: 'avg', format: fmtTps, perRequest: true },
  { key: 'tokensPerSecondP95', label: 'Output tokens/s (p95)', kind: 'avg', format: fmtTps, perRequest: true },
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
