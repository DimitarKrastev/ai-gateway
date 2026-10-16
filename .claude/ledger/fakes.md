# Fake providers reference (src/Gateway.Fakes)

One app hosts both fakes on one base URL; paths don't overlap. Point every upstream base URL of the gateway
(Anthropic API, Anthropic OAuth token, OpenAI API, ChatGPT backend, OpenAI OAuth token) at it.

## Hosting
- In tests: `await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);`
  TestServer, no port. `fake.BaseUrl` = `http://fake-providers.test`; use `fake.CreateHandler()` as the gateway's
  upstream `HttpMessageHandler` (streaming works). `fake.Anthropic` / `fake.OpenAI` are `FakeProviderState`
  (Register(FakeAccountSpec), Patch, Get, Accounts, Requests, ExpireAccessToken, Clear).
- Real port: `FakeProviderServer.StartAsync(port)` or standalone `FAKES_PORT=6202 dotnet src/Gateway.Fakes/bin/.../Gateway.Fakes.dll`
  (validates port inside `AUX_PORTS`; config `Fakes:AutoRegister` (default true), `Fakes:StreamChunkDelayMs`).
- `AutoRegister` (default true): unknown API key / bearer token creates an account on first use. ChatGPT bearer needs a `chatgpt-account-id` header to auto-register.

## Anthropic
- `POST /v1/messages`: requires `anthropic-version`, `model`, `max_tokens`, `messages`. Auth: `x-api-key` (API key account) or
  `Authorization: Bearer <sk-ant-oat01-…>` + `anthropic-beta` containing `oauth-2025-04-20` (subscription). JSON or SSE
  (`message_start` → `content_block_start/ping/content_block_delta/content_block_stop` → `message_delta` → `message_stop`).
  `message_delta.usage` is cumulative and includes input counts: take the last value, never sum with `message_start`.
  Prompt cache: when the body contains `cache_control`, prefix (all but last message) is cached per (account, model+system+first message), 5-min TTL → `cache_creation_input_tokens` first, `cache_read_input_tokens` after; a different account misses.
- Headers (OAuth): `anthropic-ratelimit-unified-status`, `-unified-{5h,7d,7d_opus}-{status,reset(unix s),utilization(fraction 0–1)}`, `-unified-representative-claim`, `-unified-reset`. (API key): `anthropic-ratelimit-{requests,tokens,input-tokens,output-tokens}-{limit,remaining,reset(RFC3339)}`. Headers reflect state after the request.
- Exhausted → 429 `rate_limit_error`, `retry-after`, unified-status `rejected`. Expired token → 401 `authentication_error`.
- `POST /v1/messages/count_tokens`, `GET /v1/models` (when `anthropic-version`/`x-api-key` present).
- `GET /api/oauth/usage` (Bearer): `{five_hour|seven_day|seven_day_opus: {utilization (percent), resets_at (ISO)}, …}`.
- `POST /v1/oauth/token` JSON or form `{grant_type: refresh_token, refresh_token}` → `{access_token, refresh_token, expires_in, …}`; rotates both (old ones invalid); unknown → 400 `invalid_grant`.
- Resets (assumed): `GET /api/oauth/usage/resets` → `{resets: [{id, expires_at}]}` sorted by expiry; `POST /api/oauth/usage/resets/{id}/redeem` → `{redeemed, usage}`; zeroes all windows and restarts them; 404 if unknown/expired.

## OpenAI
- `POST /v1/responses` (Bearer API key): JSON or SSE with `event:` + `data:` (`type`, `sequence_number`): `response.created`, `response.in_progress`, `response.output_item.added`, `response.content_part.added`, `response.output_text.delta/done`, `response.content_part.done`, `response.function_call_arguments.delta/done`, `response.output_item.done`, `response.completed` (full response with `usage{input_tokens, input_tokens_details.cached_tokens, output_tokens, output_tokens_details.reasoning_tokens, total_tokens}`). Cache key = `prompt_cache_key` or model+instructions+first input item; cached tokens count inside input_tokens. Reasoning tokens only if `reasoning` is set.
- `POST /v1/chat/completions`: JSON or `data:` chunks, `stream_options.include_usage` adds a final usage chunk (`choices: []`), ends with `data: [DONE]`.
- Headers (API key): `x-ratelimit-{limit,remaining,reset}-{requests,tokens}` (reset like `0m42s`). 429 `rate_limit_exceeded`.
- `POST /backend-api/codex/responses` (ChatGPT): Bearer JWT access token + `chatgpt-account-id` (missing → 401, mismatch → 403); `stream` must be true (else 400 `{detail}`). Headers `x-codex-{primary,secondary}-{used-percent,window-minutes,reset-after-seconds,reset-at}`. Exhausted → 429 `{error:{type:"usage_limit_reached", plan_type, resets_at, resets_in_seconds}}`.
- `GET /backend-api/wham/usage` → `{plan_type, rate_limit:{allowed, limit_reached, primary_window:{used_percent, limit_window_seconds, reset_after_seconds, reset_at}, secondary_window:{…}}, credits}`. `GET /backend-api/codex/models`, `GET /v1/models`.
- `POST /oauth/token` `{grant_type: refresh_token, refresh_token}` → `{id_token, access_token (JWT with exp and "https://api.openai.com/auth".chatgpt_account_id), refresh_token, expires_in}`; unknown/rotated → 401.
- Resets (assumed): `GET /backend-api/wham/resets`, `POST /backend-api/wham/resets/{id}/redeem` (same shapes as Anthropic).

## Default limits
OAuth accounts: Anthropic `five_hour`(300 min, +2%/request), `seven_day`(10080, +0.5%), `seven_day_opus`(10080, +0.25%); OpenAI `primary`(300, +2%), `secondary`(10080, +0.5%). Two resets (expire in 3 and 10 days). API keys: 4000 req/min, 2M tokens/min.

## Prompt triggers (last user text)
`[[tool:NAME]]`, `[[tools:N]]`, `[[no-tool]]`, `[[error:CODE]]`, `[[slow:MS]]`, `[[out:N]]`. Default: tools offered and last message is not a tool result → call first tool; else text.

## Control API `/fake-admin`
`GET|POST /{provider}/accounts` (body FakeAccountSpec: id, kind api_key|oauth, apiKey, accessToken, refreshToken, accessTokenExpiresAt, chatGptAccountId, planType, windows[{name, utilization, windowMinutes, resetsAt, incrementPerRequest}], resets[{id, expiresAt}], replaceResets, requestsLimit, tokensLimit, exhausted); `GET|PATCH /{provider}/accounts/{id}`; `POST /{provider}/accounts/{id}/expire-token`; `GET /{provider}/requests?path=&accountId=&sinceSeq=` (headers lower-cased, raw body, accountId, statusCode); `DELETE /{provider}/requests`; `POST /{provider}/reset`; `POST /reset`.
