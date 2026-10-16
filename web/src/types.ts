/** Timestamps arrive either as unix ms (analytics) or ISO strings (accounts/limits). */
export type Time = number | string | null

export type Provider = 'anthropic' | 'openai'
export type AccountKind = 'anthropic_api_key' | 'anthropic_oauth' | 'openai_api_key' | 'chatgpt_oauth'

export type LimitWindow = {
  key: string
  label: string
  utilization: number | null
  used: number | null
  limit: number | null
  remaining: number | null
  resetsAt: Time
  windowMinutes: number | null
  status: 'ok' | 'warning' | 'exhausted'
  source: 'headers' | 'usage_endpoint' | 'manual'
  updatedAt: Time
  stale: boolean
}

export type AccountLimits = {
  windows: LimitWindow[]
  exhausted: boolean
  exhaustedUntil: Time
  exhaustedReason: string | null
  unifiedStatus: string | null
  representativeClaim: string | null
  overageStatus: string | null
  planType: string | null
  refreshedAt: Time
  refreshError: string | null
  updatedAt: Time
  maxUtilization: number | null
  soonestResetAt: Time
  resets: { available: number; nextExpiresAt: Time }
}

export type Account = {
  id: number
  name: string
  provider: Provider
  kind: AccountKind
  enabled: boolean
  priority: number
  createdAt: Time
  updatedAt: Time
  credentialHint: string
  hasRefreshToken: boolean
  accessTokenExpiresAt: Time
  limits: AccountLimits | null
}

export type ResetCredit = {
  id: number
  source: 'provider' | 'manual'
  expiresAt: Time
  note: string | null
  createdAt: Time
  usedAt: Time
  usedBy: string | null
  available: boolean
  redeemable: boolean
}

export type ClientKey = {
  id: number
  name: string
  prefix: string
  createdAt: Time
  lastUsedAt: Time
  revokedAt: Time
  key?: string
}

export type Settings = { maximizeUtilization: boolean; autoUseResets: boolean; autoUseResetsIsDefault: boolean }

export type RoutingEvent = {
  id: number
  at: Time
  provider: Provider
  sessionId: string | null
  fromAccountId: number | null
  toAccountId: number | null
  reason: string
  detail: string | null
}

export type Summary = {
  from: Time
  to: Time
  requests: number
  errors: number
  rateLimited: number
  gatewayRetries: number
  costUsd: number
  unpricedRequests: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  reasoningTokens: number
  totalTokens: number
  cacheHitRate: number | null
  toolCalls: number
  avgContextTokens: number | null
  maxContextTokens: number | null
  avgLatencyMs: number | null
  tokensPerSecond: number | null
  tokensPerSecondP50: number | null
  tokensPerSecondP95: number | null
  throughputRequests: number
  sessions: number
  agents: number
  models: number
  peakConcurrentRequests: number
  peakConcurrentAgents: number
  firstSeen: Time
  lastSeen: Time
}

export type Series = { key: string; id: string | null; label: string; total: number | null }
export type Timeseries = {
  metrics: string[]
  groupBy: string
  bucket: string
  bucketMs: number
  from: Time
  to: Time
  series: Series[]
  points: Array<{ t: number } & Record<string, number | undefined>>
}

export type BreakdownRow = {
  key: string
  label: string
  requests: number
  errors: number
  costUsd: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  reasoningTokens: number
  toolCalls: number
  avgContextTokens: number | null
  maxContextTokens: number | null
  avgLatencyMs: number | null
  sessions: number
  agents: number
  firstSeen: Time
  lastSeen: Time
  /** Output-token-weighted mean tokens/second; null when no request has a throughput. */
  tokensPerSecond: number | null
  tokensPerSecondP50: number | null
  tokensPerSecondP95: number | null
  throughputRequests: number
}

export type AgentSeries = {
  key: string
  id: string
  label: string
  sessionId: string | null
  role: string | null
  client: string | null
  firstSeen: number
  lastSeen: number
  requests: number
  /** [startedAt, value] per request (thinned for very long agents). */
  points: Array<[number, number]>
}
export type AgentSeriesResult = {
  metric: string
  cumulative: boolean
  from: number
  to: number
  totalAgents: number
  hidden: number
  agents: AgentSeries[]
}

export type SessionRow = {
  provider: Provider
  sessionId: string
  firstSeen: Time
  lastSeen: Time
  requests: number
  costUsd: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  toolCalls: number
  maxContextTokens: number | null
  agents: number
  accountsUsed: number
  errors: number
  accountId: number | null
  accountName: string | null
  client: string | null
  model: string | null
  live: boolean
}

export type RequestRow = {
  id: number
  requestId: string
  attempt: number
  retried: boolean
  provider: Provider
  accountId: number | null
  accountName: string | null
  sessionId: string | null
  client: string | null
  clientVersion: string | null
  agentId: string | null
  agentRole: string | null
  method: string
  path: string
  endpoint: string
  model: string | null
  /** Advertised id Claude Code asked for when the request was bridged to an OpenAI model. */
  requestedModel?: string | null
  /** Protocol translation applied, e.g. `anthropic->openai`. */
  bridge?: string | null
  stream: boolean
  status: number | null
  errorType: string | null
  stopReason: string | null
  cancelled: boolean
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  reasoningTokens?: number
  contextTokens: number | null
  toolCalls: number
  tools: Record<string, number> | null
  costUsd: number | null
  startedAt: Time
  completedAt: Time
  latencyMs: number | null
  ttfbMs: number | null
  firstTokenMs: number | null
  generationMs: number | null
  requestedSpeed: string | null
  reportedSpeed: string | null
  speedMode: string | null
  tokensPerSecond: number | null
}

export type LiveRequest = {
  requestId: string
  provider: Provider
  accountId: number | null
  accountName: string | null
  sessionId: string | null
  agentId: string | null
  agentRole: string | null
  client: string | null
  model: string | null
  stream: boolean
  endpoint: string
  startedAt: Time
  elapsedMs: number
}

export type Live = {
  now: Time
  inFlightRequests: number
  activeAgents: number
  activeSessions: number
  agentsLast5Min: number
  sessionsLast5Min: number
  requests: LiveRequest[]
}

export type BridgeModelAccount = { id: number; name: string; kind: AccountKind; enabled: boolean; source: 'api' | 'chatgpt'; lastSeenAt: Time }

export type BridgeCatalogEntry = {
  modelId: string
  displayName: string
  description: string | null
  sources: ('api' | 'chatgpt' | 'manual')[]
  accounts: BridgeModelAccount[]
  efforts: string[]
  defaultEffort: string | null
  manual: boolean
  firstSeenAt: Time
  lastSeenAt: Time
  available: boolean
  exposed: boolean
  advertisedId: string
  advertisedName: string
  advertisedDescription: string
  defaultAdvertisedId: string
  defaultAdvertisedName: string
  defaultAdvertisedDescription: string
}

export type BridgeRefreshResult = { accountId: number; accountName: string; kind: AccountKind; ok: boolean; models: number; error: string | null }

export type BridgeCatalog = {
  models: BridgeCatalogEntry[]
  openaiAccounts: { id: number; name: string; kind: AccountKind; enabled: boolean }[]
  anthropicAccounts: number
  lastRefreshAt: Time
  refreshResults: BridgeRefreshResult[] | null
}

export type BridgeValidationError = { modelId: string; field: string; message: string }
