# Usage capture and analytics API (item 4)

## Capture (`src/Gateway/Usage`)
- `UsageObserver` (an `IRequestObserver`) records one `requests` row per upstream attempt, plus a `request_tools(request_row_id, started_at, tool, count)` row for each tool. The proxy gained an `OnRequestStarted(ctx)` hook, called before each attempt.
- `ResponseUsageParser` parses responses incrementally:
  - SSE is processed event by event. `*.delta`, `content_block_delta` and `ping` events are skipped unparsed.
  - JSON bodies are buffered up to 16 MiB.
  - Anthropic usage is cumulative, so the last snapshot wins.
  - For OpenAI, `input = input_tokens - cached_tokens` and `cacheRead = cached_tokens`.
  - Context size: Anthropic `input + cacheRead + cacheWrite`; OpenAI total input tokens.
  - Tool calls counted:
    - Anthropic: `tool_use`, `server_tool_use` and `mcp_tool_use` blocks.
    - Responses: `*_call` output items, counted once.
    - Chat: tool call fragments that carry an `id`.
- `endpoint` is one of `messages | count_tokens | responses | chat | models | other`. Only the generation endpoints (messages, responses, chat) carry tokens and an agent. `count_tokens` and `models` are logged with zero tokens.
- Cost comes from `PricingTable`: USD per MTok, matched by longest model prefix, then by family. It is an approximation and, for subscription accounts, an API-equivalent value. Override with `Gateway:Pricing:<prefix>:{Input,Output,CacheRead,CacheWrite}`. `GET /api/analytics/pricing` returns the defaults.
- `client` is derived from the User-Agent:
  - `claude-cli/x` → `claude-code`
  - `codex_cli_rs/x` or an `originator: codex*` header → `codex`
  - anything else → the first token of the User-Agent
- Agents, from `AgentIdentifier`:
  - `agent_id = provider:session:key`.
  - Codex: the key is `main`, or `subagent:<x-openai-subagent>`.
  - Other clients: the key is a hash of the system prompt prefix and the first user message prefix (512 chars each). The Claude Code billing-header block is excluded.
  - Roles:
    - no tools → `utility` (one per session)
    - `Task`/`Agent` tool offered → `main`
    - otherwise the first tool-using agent in a session is `main` and later ones are `subagent`
  - The agent "kind" key is `client:role`, e.g. `claude-code:subagent`.
- Retried attempts (429 failover) are stored with `retried=1`. They are excluded from stats unless `includeRetried=true`. The request log always shows them.
- `LiveRequests` tracks attempts in flight.

## API (cookie auth; all GET)
**Common filters**
- `from`, `to`: unix ms or ISO. Default is the last 24 h, matched on request start.
- `provider`, `accountId`, `sessionId`, `agent` (kind key), `agentId`, `model`, `client`, `includeRetried`.
- Filter values equal breakdown keys.

**`/api/analytics/summary`**
- Returns `{from, to, requests, errors, rateLimited, gatewayRetries, costUsd, unpricedRequests, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, reasoningTokens, totalTokens, cacheHitRate, toolCalls, avgContextTokens, maxContextTokens, avgLatencyMs, sessions, agents, models, peakConcurrentRequests, peakConcurrentAgents, firstSeen, lastSeen}`.

**`/api/analytics/timeseries`**
- Parameters:
  - `metric`: a comma list. Several metrics are allowed only with `groupBy=none`.
  - `groupBy`: `none | provider | account | session | agent | agentId | model | client | endpoint`.
  - `bucket`: `auto | minute | 5min | 15min | hour | 6hour | day`. `auto` keeps it to 240 points or fewer.
  - `limit`: top N series, default 8. With grouping, an "Other (n)" series is added for sum metrics.
- Metrics: `requests, errors, cost, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, reasoningTokens, totalTokens, toolCalls, contextAvg, contextMax, latencyAvg, ttfbAvg, activeAgents, activeSessions, concurrentRequests, concurrentAgents`.
  - `concurrent*`: peak overlap within the bucket, including requests still in flight.
  - `active*`: distinct agents or sessions with requests in the bucket.
- Returns `{metrics, groupBy, bucket, bucketMs, from, to, series:[{key:"s0", id, label, total}], points:[{t, s0, s1…}]}`.
  - Point keys are safe for Recharts `dataKey`.
  - Empty buckets are 0 for sums and peaks; averages are omitted.

**`/api/analytics/breakdown?by=account|session|agent|agentId|model|provider|client|endpoint|tool&limit=50`**
- Returns `{by, rows:[{key, label, requests, errors, costUsd, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, reasoningTokens, toolCalls, avgContextTokens, maxContextTokens, avgLatencyMs, sessions, agents, firstSeen, lastSeen}]}`, sorted by cost, then requests.
- Labels: account → account name; agentId → `key @ session8`.
- With `by=tool`, only `requests`, `toolCalls`, `sessions`, `agents`, `firstSeen` and `lastSeen` are filled.

**`/api/analytics/sessions?limit=50&offset=0`**
- Returns `{total, limit, offset, items:[{provider, sessionId, firstSeen, lastSeen, requests, costUsd, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, toolCalls, maxContextTokens, agents, accountsUsed, errors, accountId, accountName, client, model, live}]}`, newest first.

**`/api/analytics/requests?limit=50&before=<id>`**
- Returns `{items:[{id, requestId, attempt, retried, provider, accountId, accountName, sessionId, client, clientVersion, agentId, agentRole, method, path, endpoint, model, stream, status, errorType, stopReason, cancelled, inputTokens…contextTokens, toolCalls, tools:{name:count}, costUsd, startedAt, completedAt, latencyMs, ttfbMs}], nextBefore}`.

**`/api/analytics/live`**
- Returns `{now, inFlightRequests, activeAgents, activeSessions, agentsLast5Min, sessionsLast5Min, requests:[{requestId, provider, accountId, accountName, sessionId, agentId, agentRole, client, model, stream, endpoint, startedAt, elapsedMs}]}`.
