# Limits, resets and routing (item 5)

Code: `src/Gateway/Limits/*`, `src/Gateway/Routing/*`, migration v4 (`account_limits`, `account_limit_state`, `account_resets`, `routing_events`).

## Where limits come from
- Every proxied response: `LimitsObserver` → `LimitHeaderParser` (Claude unified-* 5h/7d/7d_opus…, Anthropic API-key requests/tokens/input/output-tokens, Codex x-codex-primary/secondary, OpenAI x-ratelimit-*). 429 → account exhausted until Retry-After / full window reset / unified reset / +60 s. 2xx clears exhaustion.
- Usage endpoints (OAuth accounts only): `/api/oauth/usage` (Claude), `/backend-api/wham/usage` (ChatGPT). Polled by `LimitsRefreshService` every `Gateway:LimitsRefreshMinutes` / `GATEWAY_LIMITS_REFRESH_MINUTES` (default 5, 0 = off; tests set 0) and queued right after an account is linked. API-key accounts only learn limits from responses.
- Window reset time passed → view shows utilization 0, status ok, `stale: true`.

## Admin API (cookie)
- `GET /api/accounts`, `GET /api/accounts/{id}` → AccountView + `limits` (null on POST/PATCH responses):
  `limits = { windows: [Window], exhausted, exhaustedUntil, exhaustedReason ("rate_limited"|"limit_reached"), unifiedStatus, representativeClaim, overageStatus, planType, refreshedAt, refreshError, updatedAt, maxUtilization, soonestResetAt, resets: { available, nextExpiresAt } }`
  `Window = { key, label, utilization (%), used, limit, remaining, resetsAt, windowMinutes, status ("ok"|"warning"|"exhausted"), source ("headers"|"usage_endpoint"|"manual"), updatedAt, stale }`.
  Keys: `five_hour`, `seven_day`, `seven_day_opus`, `seven_day_sonnet` (Claude), `primary`, `secondary` (ChatGPT), `requests`, `tokens`, `input_tokens`, `output_tokens` (API keys, per minute). Windows are ordered session → weekly → per-minute. Labels are human-readable ("Session (5h)", "Weekly (all models)", "Weekly", "Requests / min").
- `GET /api/accounts/{id}/limits` → `limits`; `POST /api/accounts/{id}/limits/refresh` → pulls usage endpoint + reset credits, returns `limits`.
- `GET /api/accounts/{id}/resets[?includeUsed=true]` (syncs provider credits first) → `[{ id, source ("provider"|"manual"), expiresAt, note, createdAt, usedAt, usedBy ("manual"|"auto"), available, redeemable }]`, soonest expiry first.
- `POST /api/accounts/{id}/resets` `{ expiresAt?, note? }` → 201 manual credit. `DELETE /api/accounts/{id}/resets/{resetId}` (manual only, else 404).
- `POST /api/accounts/{id}/resets/{resetId}/use` → `{ ok, limits }`. Provider credit → provider redeem; manual credit → local only. Both zero the windows and clear exhaustion. Errors `{error}`: 404 unknown, 409 used/expired, 410 provider no longer has it, 502 provider failure.
- `GET /api/settings` → `{ maximizeUtilization, autoUseResets, autoUseResetsIsDefault }`; `PUT /api/settings` with either field (partial). `autoUseResets` follows `maximizeUtilization` until set explicitly.
- `GET /api/routing/events?limit=100&sessionId=&accountId=` → `[{ id, at, provider, sessionId, fromAccountId, toAccountId, reason, detail }]`, newest first. Reasons: `exhausted`, `unavailable` (disabled/deleted/ineligible), `maximize`, `reset_used` (sessionId null when used from the UI).

## Routing (`PolicyAccountSelector`)
- Exhausted = exhaustion mark in the future or any window with status exhausted that hasn't reset.
- Maximize off: bound account while usable; else priority → utilization in 10 % steps → fewest active sessions → id.
- Maximize on: subscription (OAuth) accounts before API keys; soonest quota-window reset → lower utilization → priority. A healthy bound session moves only if another account resets ≥ 30 min sooner (`RebindMargin`).
- No usable account: if `autoUseResets`, redeem the provider credit closest to expiring (maximize: among OAuth accounts, before falling back to API keys) and route there; manual credits are never auto-used. Otherwise route to the account recovering first and let its 429 reach the agent.
