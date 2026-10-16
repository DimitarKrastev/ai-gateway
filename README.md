# AI Coding Gateway

A self-hosted gateway for AI coding agents (Claude Code and OpenAI Codex). The agents point at the gateway instead of
their provider and keep working as before. The gateway forwards each request through one of your **linked accounts**
(Claude / ChatGPT subscriptions and API keys), can let Claude Code use OpenAI models (translating Anthropic Messages
↔ OpenAI Responses), keeps every agent session on the same account so the provider's prompt
cache keeps working, tracks each account's limits and reset credits, can route to maximize subscription utilization,
and records every request for analytics (cost, tokens, cache, context size, tool calls, agents, concurrency…).

- Backend: .NET 10, ASP.NET Core minimal APIs, SQLite (`src/Gateway`)
- UI: React + TypeScript + Vite + Recharts (`web/`), served by the gateway itself
- Fake Anthropic/OpenAI providers for tests and end-to-end runs (`src/Gateway.Fakes`)

## Contents

- [Quick start](#quick-start)
- [Building and testing](#building-and-testing)
- [Running, stopping, restarting](#running-stopping-restarting)
- [Linking accounts](#linking-accounts)
- [Connecting Claude Code](#connecting-claude-code)
- [Connecting Codex](#connecting-codex)
- [Using OpenAI models from Claude Code](#using-openai-models-from-claude-code)
- [Routing, limits and resets](#routing-limits-and-resets)
- [Analytics](#analytics)
- [End-to-end run against fakes](#end-to-end-run-against-fakes)
- [Configuration](#configuration)
- [Architecture](#architecture)
- [Assumptions](#assumptions)

## Quick start

```sh
scripts/start.sh                      # publish (Release) and start detached on GATEWAY_PORT (default 6200)
cat data/admin-password.txt           # generated admin password (unless GATEWAY_ADMIN_PASSWORD is set)
open http://127.0.0.1:6200/           # sign in, link accounts, create a client key (Settings)
```

Then point Claude Code / Codex at the gateway (below) using the client key.

## Building and testing

Requirements: .NET SDK 10.0.103+ (see `global.json`), Node.js 20+ and npm, macOS or Linux (`lsof`, `curl`, `perl`
for the scripts).

```sh
dotnet build          # also runs `npm ci` (first time) and `npm run build` in web/ -> src/Gateway/wwwroot
dotnet test           # xUnit: unit + in-process integration tests against the fake providers
cd web && npm run lint
```

The web build is incremental (skipped when `wwwroot/index.html` is newer than every web source); pass
`-p:SkipWebBuild=true` to skip it entirely. UI development with hot reload: run the gateway, then `cd web && npm run dev`
(Vite on `WEB_PORT`, default 6201, proxying `/api`, `/anthropic`, `/openai` to the gateway on `GATEWAY_PORT`).

Tests never contact real providers: every upstream is the in-memory fake (`FakeProviderServer`).

## Running, stopping, restarting

| Command | What it does |
| --- | --- |
| `scripts/start.sh` | `dotnet publish -c Release` into `./publish`, then starts `publish/Gateway.dll` in its own session (setsid + nohup, stdin closed) so it survives the terminal or agent session ending. Refuses to start if the port is held by another process; no-op if already running. `--no-publish` reuses the last publish. |
| `scripts/stop.sh` | Stops the process recorded in `data/gateway.pid`, after checking it really is `publish/Gateway.dll` (SIGTERM, SIGKILL after 10 s). Never touches other processes. |
| `scripts/restart.sh` | `stop.sh` then `start.sh` (republishes the current checkout). |
| `scripts/status.sh` | Shows pid, port and `/api/health`. |

- Port: `GATEWAY_PORT` (default 6200). Listens on `127.0.0.1` only unless `GATEWAY_BIND` is set.
- Data dir: `GATEWAY_DATA_DIR`, default `<repo>/data` for the scripts. It holds `gateway.db` (SQLite), `keys/`
  (ASP.NET Data Protection keys that encrypt account credentials), `admin-password.txt`, `gateway.pid` and
  `logs/gateway.log`. Back it up to keep accounts and history; deleting `keys/` makes stored credentials unreadable.
- Admin password: `GATEWAY_ADMIN_PASSWORD` if set, otherwise generated on first start into
  `<data dir>/admin-password.txt`.
- Manual run without the scripts: `dotnet run --project src/Gateway` (data in `./data` relative to the working directory).

## Linking accounts

Sign in to the UI → **Accounts** → **+ Link account**. Paste whatever you have; the kind is detected automatically.
Credentials are encrypted at rest and never returned by the API (the UI shows a masked hint only).

| Kind | What to paste |
| --- | --- |
| **Claude subscription** (Pro/Max) | Either a long-lived token from `claude setup-token` (`sk-ant-oat01-…`, recommended), or Claude Code's credentials JSON `{"claudeAiOauth":{"accessToken":…,"refreshToken":…,"expiresAt":…}}`. On macOS it is in the Keychain: `security find-generic-password -s "Claude Code-credentials" -w`; on Linux in `~/.claude/.credentials.json`. |
| **Anthropic API key** | `sk-ant-api03-…` from console.anthropic.com. |
| **ChatGPT subscription** (Plus/Pro/Team, for Codex) | The contents of `~/.codex/auth.json` after `codex login` (the `tokens` object: `access_token`, `refresh_token`, `account_id`). |
| **OpenAI API key** | `sk-…` / `sk-proj-…`. |

OAuth access tokens are refreshed automatically before they expire (and once after a 401), and the rotated tokens are
stored. Because refresh tokens rotate, the copy you pasted from your own machine may stop working for the CLI there;
`claude setup-token` avoids this for Claude, and for Codex you can run `codex login` again locally afterwards (it
creates a new token family).

Each account has a **priority** (lower = preferred for new sessions), can be disabled/edited/deleted, and shows its
limits, reset credits and token expiry.

### Client keys

Agents authenticate to the gateway with a **client key** (`aigw_…`), never with your provider credentials. Create one
in **Settings → Client keys**; the plaintext is shown once together with ready-to-paste Claude Code and Codex
snippets. Without a valid key the proxy answers 401 (in the provider's error format) and tells you to create one.

## Connecting Claude Code

```sh
export ANTHROPIC_BASE_URL="http://127.0.0.1:6200/anthropic"
export ANTHROPIC_AUTH_TOKEN="aigw_…"      # your gateway client key
claude
```

or permanently in `~/.claude/settings.json`:

```json
{ "env": { "ANTHROPIC_BASE_URL": "http://127.0.0.1:6200/anthropic", "ANTHROPIC_AUTH_TOKEN": "aigw_…" } }
```

`ANTHROPIC_API_KEY=aigw_…` (sent as `x-api-key`) works too. If Claude Code is also logged in with a claude.ai
subscription, the environment token takes precedence; you can `claude /logout` to avoid the "auth conflict" notice.
The gateway strips the client key and injects the selected account's credential (API key, or subscription OAuth token
with the required `anthropic-beta: oauth-2025-04-20`). Everything else — paths, query (`?beta=true`), headers, body,
SSE streaming — is passed through unchanged.

## Connecting Codex

`~/.codex/config.toml`:

```toml
model_provider = "gateway"

[model_providers.gateway]
name = "AI Coding Gateway"
base_url = "http://127.0.0.1:6200/openai/v1"
wire_api = "responses"
env_key = "AI_GATEWAY_KEY"
```

```sh
export AI_GATEWAY_KEY="aigw_…"
codex
```

Requests to `/openai/v1/responses` go to `api.openai.com/v1/responses` when routed to an OpenAI API key, and to the
ChatGPT Codex backend (`chatgpt.com/backend-api/codex/responses`, with `chatgpt-account-id`) when routed to a ChatGPT
subscription — so one Codex config uses both kinds of account. `/openai/v1/chat/completions` and `/openai/v1/models`
are proxied as well.

## Using OpenAI models from Claude Code

The gateway can serve OpenAI models to Claude Code. Claude Code keeps speaking the Anthropic Messages API; for an
*exposed* OpenAI model the gateway translates the request to the OpenAI Responses API, sends it through one of your
linked OpenAI accounts (ChatGPT subscription via the Codex backend, or an OpenAI API key via `api.openai.com`) and
translates the answer (streaming or not) back into an Anthropic response.

**Choose the models** in the UI under **OpenAI models**:
- **Refresh** lists the models your enabled OpenAI accounts offer: `GET /v1/models` for API keys (chat/reasoning models
  only; embeddings, audio, image, moderation models and dated snapshots are filtered out) and the Codex backend's model
  list for ChatGPT accounts (with the reasoning efforts each model supports). Per-account errors are shown. Models can
  also be **added manually** by id when a list is unavailable.
- Tick **Expose** for each model Claude Code should see and **Save**. Each exposed model has an *advertised id* (default
  `anthropic-openai-<model>`, e.g. `anthropic-openai-gpt-5-codex`) and a display name, both editable. The advertised id
  must contain `claude` or `anthropic` (Claude Code's discovery keeps only ids matching `/(claude|anthropic)/i`), may
  only use `A-Z a-z 0-9 . _ : -`, and must be unique.
- `GET /anthropic/v1/models` (client key) returns the exposed models followed by the real Claude models (fetched
  through an Anthropic account when one is linked).

**Client setup** (the UI shows these snippets with your gateway URL). Gateway model discovery, recommended:

```sh
export ANTHROPIC_BASE_URL="http://127.0.0.1:6200/anthropic"
export ANTHROPIC_AUTH_TOKEN="aigw_…"                     # gateway client key
export CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1
claude                                                    # then pick the model in /model (or: claude --model anthropic-openai-gpt-5-codex)
```

or in `~/.claude/settings.json`:

```json
{ "env": {
    "ANTHROPIC_BASE_URL": "http://127.0.0.1:6200/anthropic",
    "ANTHROPIC_AUTH_TOKEN": "aigw_…",
    "CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY": "1" } }
```

Alternative without discovery — one extra picker entry (not subject to the id filter, but the gateway still only
bridges exposed advertised ids):

```sh
export ANTHROPIC_CUSTOM_MODEL_OPTION="anthropic-openai-gpt-5-codex"
export ANTHROPIC_CUSTOM_MODEL_OPTION_NAME="GPT-5 Codex (OpenAI)"
export ANTHROPIC_CUSTOM_MODEL_OPTION_DESCRIPTION="OpenAI via the AI Coding Gateway"
```

Claude Code also makes background calls (titles, summaries) with its small model. If no Anthropic account is linked,
point those at an exposed model too: `ANTHROPIC_DEFAULT_HAIKU_MODEL` (and, for older versions,
`ANTHROPIC_SMALL_FAST_MODEL`) = an advertised id.

**Routing and analytics.** A bridged request uses the same client keys, Claude Code session id and account selector as
other traffic, with provider `openai`: the session sticks to one OpenAI account (the session id is also sent as
`prompt_cache_key` so OpenAI's prompt cache is reused), only accounts that listed the model are used (any OpenAI account
for manual models), 429s fail over to another account, and limits are read from the upstream headers. Requests are
recorded as OpenAI traffic with the **actual OpenAI model** in *model* and the **advertised id** in *requested model*
(request log: "gpt-5-codex via anthropic-openai-gpt-5-codex"; Analytics can group/filter by requested model).
Tokens (incl. cached and reasoning), output speed, speed mode and tool calls come from the OpenAI stream; agent roles
and per-agent series use the Claude Code request, so they work as for Claude models.

**How it translates.** System prompt → `instructions`; text, images (`input_image`) and PDFs/documents (`input_file`)
→ input messages; `tool_use`/`tool_result` → `function_call`/`function_call_output` (tool results with images keep
them); tools → function tools, `tool_choice` and `disable_parallel_tool_use` mapped; extended thinking/effort →
`reasoning.effort` (`output_config.effort` low/medium/high/max → low/medium/high/xhigh; otherwise budget ≤4k low,
≤16k medium, else high; clamped to the efforts the model supports) and `reasoning.summary`; `speed: "fast"` →
`service_tier: "priority"`. Back: reasoning → `thinking` blocks, text → `text`, function calls → `tool_use` with
incremental `input_json_delta`, stop reasons (`tool_use`, `max_tokens`, `refusal`, `end_turn`), usage (`input_tokens`
excludes cached tokens, which are reported as `cache_read_input_tokens`; output includes reasoning). Upstream errors
come back as Anthropic errors (`{"type":"error","error":{…}}`, status kept), mid-stream failures as an SSE `error` event.

**Limitations** (what cannot be translated faithfully):
- Web search works: Claude Code's WebSearch sends Anthropic's `web_search` server tool, which becomes OpenAI's built-in
  `web_search` tool; each search comes back as `server_tool_use` + `web_search_tool_result` (result titles from OpenAI's
  citations where available, otherwise from the URL) and is counted in `usage.server_tool_use.web_search_requests`.
  `allowed_domains` and `user_location` carry over; `blocked_domains` and `max_uses` have no OpenAI equivalent.
- Other Anthropic server tools (web fetch, code execution) are dropped. Claude Code's WebFetch does not depend on them:
  it fetches pages itself after a domain check against `api.anthropic.com` (not routed through the gateway), so a
  "Claude Code is unable to fetch from …" error is that check, on any model. Tool names OpenAI rejects are shortened
  and mapped back transparently.
- `count_tokens` for bridged models is a local estimate (~4 characters per token); no provider is called.
- `cache_control` markers are ignored (OpenAI caches prompt prefixes automatically); `cache_creation_input_tokens` is 0.
- `temperature`, `top_p`, `top_k`, `stop_sequences`, `context_management` and `anthropic-beta` features are dropped.
- Thinking is shown as OpenAI's reasoning *summary*, not the full reasoning. The encrypted reasoning travels in the
  thinking block's `signature` (`bridge1:…`), so it round-trips only for thinking produced by the bridge; thinking from a
  real Claude model in the same conversation is dropped when switching to an OpenAI model.
- ChatGPT (Codex backend) requests are always streamed upstream (non-streaming clients get the aggregated message), use
  `store: false`, always carry `instructions` and never `max_output_tokens`, so `max_tokens` is not enforced there; API
  key requests send `max_tokens` as `max_output_tokens`. Requests to the Codex backend carry Codex-style headers
  (`originator: codex_cli_rs`, `session_id`).
- Images/files referenced by Anthropic file ids cannot be forwarded; base64 and URL sources can.

## Routing, limits and resets

**Sticky sessions.** The session id comes from Claude Code's `metadata.user_id` (`…_session_<uuid>`) or the
`x-claude-code-session-id` header, and from Codex's `session_id` / `conversation_id` header or `prompt_cache_key`.
(Fallback: a hash of client key + system prompt + first message, ignoring Claude Code's per-request billing header.)
The first request binds the session to an account; later requests use the same account so the prompt cache is kept.

**Default mode.** A session stays on its account while that account is usable. When it is exhausted (provider 429 or a
limit at 100 %) or disabled, the session moves to another eligible account (priority → utilization → fewest active
sessions). A 429 is retried transparently on another account when one is available.

**Limits.** For every account the gateway shows the provider's limits and when they reset:
- Claude subscription: session (5 h), weekly (all models), weekly Opus/Sonnet, from `anthropic-ratelimit-unified-*`
  response headers and the OAuth usage endpoint (`/api/oauth/usage`, polled every 5 min).
- ChatGPT subscription: 5 h (primary) and weekly (secondary) windows, from `x-codex-*` headers and `/backend-api/wham/usage`.
- API keys: requests/tokens per minute from `anthropic-ratelimit-*` / `x-ratelimit-*` headers.

**Resets.** Each account lists its available reset credits (with expiry) and has a **Use reset** button; credits can
also be added manually (e.g. ones granted by the provider out of band). Using one redeems it at the provider (or, for
manual credits, locally), zeroes the limit windows and clears exhaustion.

**Maximize utilization** (Settings, off by default). New and existing sessions go to the subscription account whose
quota window resets soonest (quota that would expire unused gets used first; API keys only when no subscription is
usable). A healthy session only moves when another account resets at least 30 minutes sooner (anti-flapping); such a
move costs one prompt-cache miss. When every subscription account is exhausted and **auto-use resets** is on (default
when maximizing), the gateway redeems the provider reset credit closest to expiring and routes there. Manual credits are
never used automatically. Every session move and reset use is listed under **Sessions → Session moves**.

## Analytics

The UI's **Overview**, **Analytics**, **Sessions** and **Requests** pages show, over any time range and broken down by
provider, account, session, agent type, agent instance, model, speed mode, model + mode, client or tool:
cost, requests, errors, input/output/cache-read/cache-write/reasoning tokens, cache hit rate, context size (avg/max),
tool calls, latency/TTFB, output tokens/second, active and concurrent agents/requests, plus a live in-flight view and a
per-request log.

Notes:
- **Cost** uses a built-in table of public API list prices (USD per million tokens, longest model-prefix match). For
  subscription accounts it is the *API-equivalent* cost. Prices are approximations; override per model prefix with
  `Gateway__Pricing__<prefix>__{Input,Output,CacheRead,CacheWrite}` (or `Gateway:Pricing` in appsettings). Current
  table: `GET /api/analytics/pricing`.
- **Agents** are inferred (providers don't label them). Codex: one main agent per session (`x-openai-subagent` marks
  subagents). Claude Code: an agent is identified by its system prompt + first user message within a session; a request
  without tools is a *utility* call (titles, summaries), an agent that offers the `Task`/`Agent` tool is *main*, other
  tool-using agents are *subagents*. Concurrency counts overlapping in-flight requests/agents.
- **Output speed** (tokens/second) per request = output tokens ÷ generation time. Streaming: from the first output
  token event to the end of the stream (queueing and prompt processing excluded). Non-streaming: the whole latency, an
  approximation that reads slower. Only successful, non-cancelled requests with output count. Aggregates are the
  output-token-weighted mean plus p50/p95; the Analytics page charts them over time and per model × mode.
- **Speed modes** are taken from the request (Anthropic `speed: "fast"`; OpenAI/Codex `service_tier` `fast`/`priority`
  → fast, `flex` → flex, absent/`auto`/`default` → standard) and from the response (Anthropic `usage.speed`, OpenAI
  `service_tier`), which wins when present. Raw values are kept per request. Requests recorded before this was tracked
  show mode `unknown` and no output speed.
- **Per agent:** grouping a chart by *Agent (each instance)* draws one line per agent instance with a point per request
  (`/api/analytics/agent-series`), so you see when each agent appeared and, e.g., how its context grew; sum metrics can be
  shown as running totals. The 12 busiest agents are drawn (filter by session to narrow); click legend entries to hide
  lines. Metrics without a per-request value (concurrency, active counts) fall back to bucketed series per agent. A
  client-sent `x-claude-code-agent-id` header, when present, identifies a Claude Code agent instance directly; a Codex
  `prompt_cache_key` that differs from the session id marks a separate thread.
- `count_tokens` and `models` calls are logged as requests with zero tokens and no session/agent.
- Claude Code requests for exposed OpenAI models are counted as OpenAI traffic: *model* is the OpenAI model actually
  used, *requested model* the advertised id (see [Using OpenAI models from Claude Code](#using-openai-models-from-claude-code)).
- Requests the gateway retried on another account after a 429 are shown in the request log but excluded from totals.
- **Live updates:** the UI keeps a SignalR connection to `/api/live` (admin session required). The gateway pushes the
  in-flight snapshot as requests start and finish, and a "changed" notice (usage, limits, accounts, routing) at most every
  250 ms; pages refetch what they show, and a range that ends *now* slides forward with each new request (at most once a
  second). The top bar shows **Live** while connected; without the connection pages fall back to periodic refresh.
  The Overview's **Live throughput** panel is a scrolling one-second chart of tokens generated (↓) and prompt tokens
  sent (↑) across all accounts, over the last 1, 5 or 10 minutes, plus requests in flight. Output is counted while it
  streams (streamed text ÷ 4); when the request completes, output that was not streamed (hidden reasoning, estimate
  error) is spread over its generation time and prompt tokens are placed at the second the request was sent, and the
  corrected seconds are pushed again. It is kept in memory only (`GET /api/analytics/meter`).
  For second-level detail pick the **5m** or **15m** range (auto bucket 5 s; **1h** gets 15 s) or a 5s/15s/30s bucket on
  the Analytics page.

## End-to-end run against fakes

```sh
scripts/e2e.sh            # build, run, check, tear down
scripts/e2e.sh --keep     # leave the e2e gateway + fakes running to inspect the UI (prints URL, password, stop command)
```

The script picks two free ports in `AUX_PORTS` (default 6202-6219), starts the standalone fake providers and a separate
gateway (temporary data dir, all upstreams → fakes), links all four account kinds and replays realistic traffic
(`e2e/run.mjs`, no dependencies):
- Claude Code: `claude-cli` headers, session metadata, billing header, main agent → `Task` subagent → tool results,
  a utility call, `count_tokens`, streaming and non-streaming, prompt caching;
- Codex: `codex_cli_rs` headers, Responses API streaming with `shell` function calls and outputs, through the OpenAI API
  key and through the ChatGPT subscription;
- Claude Code on exposed OpenAI models: catalog refresh, exposure, `/v1/models` discovery, a bridged session with
  thinking, Task subagent, tool results, utility and `count_tokens` through ChatGPT and the OpenAI API key, validating
  every returned Anthropic stream;
- concurrent agents; maximize mode moving a session, failover on exhaustion and automatic use of the reset closest to
  expiring; a manual reset.

It then checks, via the gateway's APIs and the fakes' request logs, that tokens (incl. cache), tool calls, requests and
sessions per provider equal what the fakes reported, cost is non-zero, agents/models/tools/accounts appear, sessions are
sticky, credentials were injected (the client key never reaches a provider), limits are populated and routing/reset
events happened. Only processes started by the script are stopped.

The e2e fakes generate at `E2E_FAKE_TPS` output tokens/s (default 1500; fast mode 2.5×). Prompt triggers
`[[tps:N]]` (speed) and `[[ttft:MS]]` (wait before the first token) set them per request; fakes echo fast mode as
`usage.speed` (Anthropic) or `service_tier: "priority"` (OpenAI).

The fakes can also be run alone: `FAKES_PORT=6202 dotnet src/Gateway.Fakes/bin/Release/net10.0/Gateway.Fakes.dll`
(control API under `/fake-admin`).

## Configuration

Values come from `Gateway:*` configuration (appsettings / `Gateway__X` env) first, then these environment variables,
then defaults.

| Setting | Env var | Default |
| --- | --- | --- |
| Listen port | `GATEWAY_PORT` | `6200` |
| Bind address | `GATEWAY_BIND` | `127.0.0.1` |
| Data directory | `GATEWAY_DATA_DIR` | `./data` (scripts: `<repo>/data`) |
| UI files | `GATEWAY_WEB_ROOT` | `wwwroot` next to `Gateway.dll` |
| Admin password | `GATEWAY_ADMIN_PASSWORD` | generated into `<data dir>/admin-password.txt` |
| Codex client version reported to the ChatGPT backend | `GATEWAY_CODEX_CLIENT_VERSION` | `0.163.0` (the backend hides models from older versions) |
| Usage-endpoint polling (minutes, 0 = off) | `GATEWAY_LIMITS_REFRESH_MINUTES` | `5` |
| Anthropic API base | `GATEWAY_UPSTREAM_ANTHROPIC_API` | `https://api.anthropic.com` |
| Anthropic OAuth token host | `GATEWAY_UPSTREAM_ANTHROPIC_AUTH` | `https://console.anthropic.com` |
| OpenAI API base | `GATEWAY_UPSTREAM_OPENAI_API` | `https://api.openai.com` |
| ChatGPT backend | `GATEWAY_UPSTREAM_CHATGPT` | `https://chatgpt.com` |
| OpenAI OAuth token host | `GATEWAY_UPSTREAM_OPENAI_AUTH` | `https://auth.openai.com` |

The gateway contacts a provider only on behalf of a linked account (proxied requests, token refresh, usage polling).

## Architecture

```
Claude Code ─┐  /anthropic/*                  ┌─ api.anthropic.com (API key | OAuth)
             ├─► Gateway ─ client-key auth ─ session id ─ account selector ─ credential injection ─┤
Codex ───────┘  /openai/*                     └─ api.openai.com | chatgpt.com/backend-api/codex
                    │ observers (stream is never buffered for the client)
                    ├─ UsageObserver  → requests table (tokens, cost, tools, agents)
                    └─ LimitsObserver → account limits / exhaustion
   UI (React) ── /api/* (cookie auth): accounts, client keys, limits, resets, settings, analytics, routing events

   /anthropic/v1/messages for an exposed OpenAI model: Anthropic → Responses translation → OpenAI account → translated back
```

- `src/Gateway/Proxy` — transparent streaming proxy, client keys, session identification, OAuth token refresh.
- `src/Gateway/Accounts` — linked accounts (Data Protection-encrypted credentials), credential parsing.
- `src/Gateway/Routing` — `PolicyAccountSelector` (sticky / maximize / reset use), routing events, settings.
- `src/Gateway/Limits` — header parsing, usage-endpoint polling, reset credits.
- `src/Gateway/Usage` — incremental SSE/JSON usage parsing, pricing, agent identification, analytics queries.
- `src/Gateway/Bridge` — OpenAI model catalog/selection, `/anthropic/v1/models` merge, Anthropic Messages ↔ OpenAI
  Responses translation (requests, JSON, SSE, errors, token estimate) for exposed models.
- `src/Gateway/Infrastructure` — options, SQLite with append-only migrations.

## Assumptions

- **Single user.** One admin password protects the UI and `/api`; agents use separate revocable client keys. The
  gateway listens on localhost by default.
- **Subscriptions are linked by pasting tokens** (Claude Code credentials / `claude setup-token`, Codex `auth.json`)
  rather than an in-app OAuth login flow, since those flows are tied to the official clients.
- **Reset credits.** No public provider API for "limit resets" is known, so they are modelled as an optional provider
  capability (list credits with expiry, redeem one) at `…/api/oauth/usage/resets` (Anthropic) and
  `…/backend-api/wham/resets` (ChatGPT), implemented by the fakes, plus manually added credits. If a provider has no
  such endpoint the gateway simply shows no provider credits.
- Limits and usage-endpoint formats follow what the official clients receive today
  (`anthropic-ratelimit-unified-*`, `/api/oauth/usage`, `x-codex-*`, `/backend-api/wham/usage`); unknown fields are ignored.
- Costs are API list-price approximations; agent roles are heuristics (see [Analytics](#analytics)).
- The gateway asks providers for uncompressed responses so usage can be read from the stream as it passes.
- The ChatGPT Codex backend's request rules (streaming only, `store: false`, `instructions` required, no
  `max_output_tokens`/`temperature`) and its model list (`/backend-api/codex/models?client_version=…`) follow what the
  Codex CLI does today; the fakes enforce them.
