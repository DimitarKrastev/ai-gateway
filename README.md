# AI Coding Gateway

A self-hosted gateway for AI coding agents (Claude Code and OpenAI Codex). The agents point at the gateway instead of
their provider and keep working as before. The gateway forwards each request through one of your **linked accounts**
(Claude / ChatGPT subscriptions and API keys), keeps every agent session on the same account so the provider's prompt
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
provider, account, session, agent, model, client or tool:
cost, requests, errors, input/output/cache-read/cache-write/reasoning tokens, cache hit rate, context size (avg/max),
tool calls, latency/TTFB, active and concurrent agents/requests, plus a live in-flight view and a per-request log.

Notes:
- **Cost** uses a built-in table of public API list prices (USD per million tokens, longest model-prefix match). For
  subscription accounts it is the *API-equivalent* cost. Prices are approximations; override per model prefix with
  `Gateway__Pricing__<prefix>__{Input,Output,CacheRead,CacheWrite}` (or `Gateway:Pricing` in appsettings). Current
  table: `GET /api/analytics/pricing`.
- **Agents** are inferred (providers don't label them). Codex: one main agent per session (`x-openai-subagent` marks
  subagents). Claude Code: an agent is identified by its system prompt + first user message within a session; a request
  without tools is a *utility* call (titles, summaries), an agent that offers the `Task`/`Agent` tool is *main*, other
  tool-using agents are *subagents*. Concurrency counts overlapping in-flight requests/agents.
- `count_tokens` and `models` calls are logged as requests with zero tokens and no session/agent.
- Requests the gateway retried on another account after a 429 are shown in the request log but excluded from totals.

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
- concurrent agents; maximize mode moving a session, failover on exhaustion and automatic use of the reset closest to
  expiring; a manual reset.

It then checks, via the gateway's APIs and the fakes' request logs, that tokens (incl. cache), tool calls, requests and
sessions per provider equal what the fakes reported, cost is non-zero, agents/models/tools/accounts appear, sessions are
sticky, credentials were injected (the client key never reaches a provider), limits are populated and routing/reset
events happened. Only processes started by the script are stopped.

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
```

- `src/Gateway/Proxy` — transparent streaming proxy, client keys, session identification, OAuth token refresh.
- `src/Gateway/Accounts` — linked accounts (Data Protection-encrypted credentials), credential parsing.
- `src/Gateway/Routing` — `PolicyAccountSelector` (sticky / maximize / reset use), routing events, settings.
- `src/Gateway/Limits` — header parsing, usage-endpoint polling, reset credits.
- `src/Gateway/Usage` — incremental SSE/JSON usage parsing, pricing, agent identification, analytics queries.
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
