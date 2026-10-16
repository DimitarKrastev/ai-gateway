# Accounts, client keys and proxy (item 3)

## Admin API (cookie login)
- `GET /api/accounts` → `[AccountView]`; `GET /api/accounts/{id}`; `POST /api/accounts` (201); `PATCH /api/accounts/{id}`; `DELETE /api/accounts/{id}` (204).
  - Input `AccountInput`: `name, kind, enabled, priority (default 100, lower = preferred), apiKey, accessToken, refreshToken, expiresAt, chatGptAccountId, credentialsJson`.
  - `credentialsJson` = whatever the user pastes: raw key/token (kind inferred: `sk-ant-oat`→anthropic_oauth, `sk-ant-`→anthropic_api_key, `sk-`→openai_api_key, JWT with chatgpt claim→chatgpt_oauth), Claude Code creds `{"claudeAiOauth":{accessToken,refreshToken,expiresAt(ms)}}`, Codex `auth.json` (`tokens.{access_token,refresh_token,id_token,account_id}` or `OPENAI_API_KEY`). Explicit fields override the blob. ChatGPT account id falls back to the JWT claim `https://api.openai.com/auth.chatgpt_account_id`; expiry falls back to JWT `exp`. Refresh-token-only → refreshed before first use.
  - PATCH: secrets present → re-parsed (kind may change within the same provider only); otherwise name/enabled/priority.
  - `AccountView`: `id, name, provider, kind, enabled, priority, createdAt, updatedAt, credentialHint (masked), hasRefreshToken, accessTokenExpiresAt`.
- `GET /api/client-keys` → `[{id,name,prefix,createdAt,lastUsedAt,revokedAt}]`; `POST {name}` → adds `key` (plaintext, only time shown, `aigw_` + 32 base62); `DELETE /{id}` revokes.

## Proxy
- `/anthropic/{path}` → `Upstreams.AnthropicApi/{path}` (query preserved, e.g. `?beta=true`).
- `/openai/{path}`: API-key accounts → `Upstreams.OpenAIApi/{path}`; ChatGPT accounts: `v1/responses*`, `v1/models` → `ChatGptBackend/backend-api/codex/{rest}`; `backend-api/*` → `ChatGptBackend/backend-api/*` (ChatGPT only). Eligibility in `UpstreamRouter.IsEligible`.
- Codex config: custom provider `base_url = "http://127.0.0.1:6200/openai/v1"`, `wire_api = "responses"`, `env_key` holding the gateway key. Claude Code: `ANTHROPIC_BASE_URL=http://127.0.0.1:6200/anthropic` + `ANTHROPIC_AUTH_TOKEN` (Bearer) or `ANTHROPIC_API_KEY` (x-api-key) = gateway key.
- Client key: Bearer, or `x-api-key` for Anthropic. Invalid/missing → 401 in the provider's error format; message says to create a key in the UI when none exist (no open mode).
- Stripped request headers: hop-by-hop, host, content-length, authorization, x-api-key, cookie, chatgpt-account-id, **accept-encoding** (upstream answers uncompressed so observers can parse). Injected: x-api-key / Bearer (+ `anthropic-beta` gets `oauth-2025-04-20` for Claude OAuth, removed for API keys) / Bearer + `chatgpt-account-id`. Response headers copied except hop-by-hop and set-cookie; body streamed with flush per read (16 KiB buffer).
- No eligible account → 503. Upstream unreachable → 502. 429 → account cooldown (Retry-After or 60 s, in-memory `AccountCooldowns`) and failover to another account (max 3 attempts); if none, 429 passes through.
- OAuth refresh (`TokenRefresher`): when `expiresAt - 2 min <= now`, or once after an upstream 401. Anthropic `POST {AnthropicAuth}/v1/oauth/token` JSON with Claude Code client id; OpenAI `POST {OpenAIAuth}/oauth/token` with Codex client id. Per-account lock; rotated tokens persisted.

## Sessions
- `SessionIdentifier`: Anthropic `x-claude-code-session-id` → `metadata.user_id` (`…_session_<id>` or JSON `{session_id}`); OpenAI headers `session_id`/`conversation_id`/`x-session-id` → body `prompt_cache_key`; fallback `h-<sha256(clientKeyId|system/instructions|first message)>`. Source recorded (`header|metadata|prompt_cache_key|derived`).
- `session_bindings(provider, session_id, account_id, client_key_id, first_seen, last_seen, request_count)`; timestamps unix ms (convention for all tables).
- `IAccountSelector.SelectAsync(AccountSelectionRequest{Provider, SessionId, UpstreamPath, ClientKeyId, ExcludedAccountIds}) → AccountSelection{Account, NewSession, Rebound}`, `ReportRateLimited(account, retryAfter)`. Default `StickyAccountSelector`: bound account if enabled+eligible+not cooling; else lowest priority, then fewest sessions active in last hour, then id. Serialized by a semaphore.

## Observer hook
- Register `IRequestObserver` singletons (any number). Per upstream attempt: `OnResponseStarted(ctx)` (status + `ResponseHeaders`, lower-cased), `OnResponseChunk(ctx, span)` (in order, as streamed), `OnCompletedAsync(ctx)` (always, also on cancel/error).
- `ProxiedRequestContext`: RequestId (shared by attempts), Attempt, Provider, Account (LinkedAccount), SessionId/Source, NewSession, Rebound, ClientKeyId, Method, Path, Query, UpstreamUri, RequestHeaders (sanitized), RequestBody (bytes), StartedAt, ResponseStartedAt, CompletedAt, StatusCode, ResponseBytes, RetriedByGateway (failed-over attempt, not sent to client), ClientCancelled, Error, Items (scratch).
