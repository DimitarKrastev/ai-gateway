# Ledger

## Item 1 — Scaffold
- **Status:** done
- **Evidence:** `dotnet build` 0 warnings/0 errors; `dotnet test` 6/6 passed; `npm run build` + `npm run lint` (oxlint) clean; fresh `git clone` → build + test pass; published build run briefly on 6202: health 200, `/` and `/accounts` serve SPA, `/api/auth/me` 401, `/openai/*` 404. Commit 63628ce.
- **Decisions:** Vite builds into `src/Gateway/wwwroot` (gitignored); MSBuild targets in `Gateway.csproj` run `npm ci` (if no node_modules) and incremental `npm run build` on every build, `-p:SkipWebBuild=true` skips; built assets added to Content for publish. Bind 127.0.0.1 by default (`GATEWAY_BIND`). Pinned Sqlite 10.0.3 + `SQLitePCLRaw.bundle_e_sqlite3` 2.1.14 (fixes NU1903). Data Protection keys in `<data>/keys`.
- **Interface changes:** `GatewayOptions` (Port, BindAddress, DataDir, WebRoot, AdminPassword; `Gateway:*` config → env → default). `Database` singleton: `Open()`, `Migrate()`, append to `Database.Migrations` (v1 = `settings` table), `Get/SetSetting`. `UseApiLoginRequirement` gates all `/api/*` except health and auth (allowlist in `AuthEndpoints.AnonymousApiPaths`). SPA fallback excludes `/api`, `/anthropic`, `/openai`.
- **Follow-ups:** README sections marked "To be completed". `Gateway.Fakes` is a placeholder Program.
- **Learnings:** Runtime installed is 10.0.3 — pin Microsoft.* packages to 10.0.3. Tests: `GatewayFactory` (temp DataDir, `AdminPassword`, `CreateLoggedInClientAsync()`).

## Item 2 — Fake providers
- **Status:** done
- **Evidence:** `dotnet build` (full, incl. web) succeeded; `dotnet test` 19/19 passed (13 new in `FakeProvidersTests`); standalone run on 6219: SSE stream + admin API via curl OK, port outside AUX_PORTS rejected (exit 1), process stopped. Commit d2c1d8d.
- **Decisions:** One app hosts both fakes (paths disjoint; `/v1/models` dispatched by `anthropic-version`/`x-api-key`). Tests use in-memory TestServer (no ports). Explicit `Main` in Fakes — .NET 10 makes top-level `Program` public and it clashed with the gateway's. Removed Fakes `appsettings.json`. Rate-limit headers report post-request state.
- **Interface changes:** `FakeProviderServer.StartInMemoryAsync/StartAsync(port)`, `.CreateHandler()`, `.BaseUrl`, `.Anthropic/.OpenAI` (`FakeProviderState`). Reset credits: `GET /api/oauth/usage/resets`, `POST …/{id}/redeem` (Anthropic); `GET /backend-api/wham/resets`, `POST …/{id}/redeem` (ChatGPT). Full reference: `.claude/ledger/fakes.md`.
- **Follow-ups:** The gateway needs configurable base URLs: Anthropic API, Anthropic token, OpenAI API, ChatGPT backend, OpenAI token, plus an injectable upstream `HttpMessageHandler` for tests. Anthropic `message_delta` usage is cumulative.
- **Learnings:** Tests reference `Gateway.Fakes`. Prompt triggers `[[tool:X]]`, `[[tools:N]]`, `[[no-tool]]`, `[[error:CODE]]`, `[[slow:MS]]`, `[[out:N]]`. AutoRegister (default on) creates accounts for unknown credentials.

## Item 3 — Accounts + proxy core
- **Status:** done
- **Evidence:** `dotnet build` (full, incl. web) succeeded; `dotnet test` 34/34 passed, stable over 4 runs (15 new in `AccountsApiTests`, `ProxyTests`). Commit 0da20bc. New: `src/Gateway/{Accounts,ClientKeys,Proxy}/*`, migration v2 in `Database.cs`, `UpstreamOptions` in `GatewayOptions.cs`.
- **Decisions:** Agents must present a gateway client key (`aigw_…`); with none created the 401 says how to create one, so there is no open mode. Upstream asked for identity encoding so observers can parse. Failover on 429 uses in-memory cooldowns, which item 5 may replace. Account `priority`: lower number is preferred, default 100. Timestamps are stored as unix ms.
- **Interface changes:** `/api/accounts`, `/api/client-keys`, `/anthropic/*`, `/openai/*`. Upstream config is `Gateway:Upstreams:{AnthropicApi,AnthropicAuth,OpenAIApi,ChatGptBackend,OpenAIAuth}` or the matching `GATEWAY_UPSTREAM_*` env vars. Extension points are `IAccountSelector` (default `StickyAccountSelector`) and `IRequestObserver` (register as singletons). Tables: `accounts`, `client_keys`, `session_bindings`. Full detail: `.claude/ledger/proxy.md`.
- **Follow-ups:** Item 4 implements an `IRequestObserver` and should skip `RetriedByGateway` attempts or count them separately. Item 5 replaces or decorates `StickyAccountSelector` and parses limits from `ctx.ResponseHeaders`. The README needs the client setup from proxy.md.
- **Learnings:** Tests use `ProxyGatewayFactory(fake)`, which points all upstreams at the fake and injects its handler, plus the helpers `CreateClientKeyAsync`, `LinkAsync` and `ProxyRequests.*`.

## Item 4 — Usage capture + analytics API
- **Status:** done
- **Evidence:** `dotnet build` (full, incl. web) succeeded; `dotnet test` 50/50 passed, 3 consecutive runs (16 new: `UsageParserTests`, `UsageAnalyticsTests`). Commit 7a862ae. New: `src/Gateway/Usage/*`; migration v3 (`requests`, `request_tools`); `OnRequestStarted` hook in `RequestObserver.cs`/`ProxyHandler.cs`; `ProxyGatewayFactory.ExtraServices`.
- **Decisions:** One row per upstream attempt. Retried 429 attempts are excluded from stats by default (`includeRetried=true` includes them) but always shown in the log. `count_tokens`/`models` are logged with zero tokens. Cost is the API list price, approximate and API-equivalent for subscriptions, and config-overridable. Agent heuristics and the shape of every endpoint are in `.claude/ledger/analytics.md`.
- **Interface changes:** `/api/analytics/{summary,timeseries,breakdown,sessions,requests,live,pricing}`; timeseries returns `series[{key:"s0",id,label,total}]` with flat `points[{t,s0…}]`. `IRequestObserver.OnRequestStarted`.
- **Follow-ups:** For the README: the agent heuristic and that pricing is approximate. The derived-session fallback (item 3) hashes the system prompt including Claude Code's per-request billing header, so a request without `metadata.user_id` gets a fresh session if that header varies. Rejected requests (401/503 before account selection) are not recorded.
- **Learnings:** Analytics rows are written before the proxied response completes. The fake's prompt cache key includes the system prompt, so keep test system prompts stable.

## Item 5 — Limits, resets, routing policy
- **Status:** done
- **Evidence:** full `dotnet build` (with web) has 0 warnings. `dotnet test` passed 64/64 on 3 runs; the 14 new tests are in `LimitsRoutingTests`. Commit 2c2c65e: new `src/Gateway/{Limits,Routing}/*`, migration v4, `PolicyAccountSelector` replaces `StickyAccountSelector`/`AccountCooldowns`, `SessionIdentifier` fallback fix, `GatewayOptions.LimitsRefreshMinutes`.
- **Decisions:** Limits come from response headers on every request and from usage endpoints (OAuth accounts, polled every 5 min; `GATEWAY_LIMITS_REFRESH_MINUTES`, 0 = off, tests use 0). A 429 marks the account exhausted until Retry-After or the window resets; a 2xx clears it. In maximize mode subscriptions come before API keys, the soonest quota reset wins, and a session only moves for a reset at least 30 min sooner (anti-flap). `autoUseResets` follows `maximizeUtilization` until set, and auto-use only redeems provider credits. With everything exhausted and no reset, the request goes to the account that recovers first, so the provider's 429 reaches the agent. The derived session hash now uses text only and drops the billing-header block.
- **Interface changes:** `AccountView.limits`; `/api/accounts/{id}/limits[/refresh]`, `/api/accounts/{id}/resets[/{rid}/use]`, `/api/settings`, `/api/routing/events`. Shapes: `.claude/ledger/limits.md`.
- **Follow-ups:** UI item: account cards (windows, exhausted badge, resets with Use button, refresh), settings toggles, routing-event list. README: resets are an assumed provider API; manual credits; maximize semantics.
- **Learnings:** Fake windows with `IncrementPerRequest: 0` keep routing tests deterministic.

## Item 6 — Web UI
- **Status:** done
- **Evidence:** `npm run build` + `npm run lint` (oxlint) clean; `dotnet build` 0 warnings; `dotnet test` 64/64. Browser pass (Playwright, light + dark + 390px mobile) against gateway on 6205 + standalone fakes on 6204 seeded with Claude Code/Codex-shaped traffic (5 accounts, 4 sessions, subagents, tool calls, parallel requests, an error): every page rendered with data, use-reset/link/create-key flows worked, only console error is the expected pre-login 401. Processes stopped. Commit dbf94f6 (`web/src/**`, `web/vite.config.ts`).
- **Decisions:** Global time range (1h/24h/7d/30d/custom, auto re-anchors every 30 s) in `rangeContext`; `useApi` keeps stale data while refetching; session detail charts span the session's own first/last seen (from=0 would create 20k buckets); request log pages all history via `nextBefore`; confirm dialogs are custom modals (no `window.confirm`); chunk warning limit raised to 1000 kB (Recharts).
- **Interface changes:** none (UI only consumes item 3–5 APIs). Routes: `/`, `/analytics`, `/accounts`, `/sessions`, `/sessions/:provider/:sessionId`, `/requests`, `/settings`, `/login`.
- **Follow-ups:** Item 7: running `dotnet src/Gateway/bin/.../Gateway.dll` from the repo root serves 404 for `/` — content root is the cwd, so set `GATEWAY_WEB_ROOT` or run from the publish dir. Parallel `[[slow:2500]]` requests logged ~30 s latency — worth a look in e2e (fake or proxy timing).
- **Learnings:** Playwright 1.56 npm + cached `chromium_headless_shell-1248` (pass `executablePath`) works offline; keep it in the scratchpad.

## Item 7 — Fixes from item 6 (latency, web root)
- **Status:** done
- **Evidence:** `dotnet build` (full, incl. web) 0 warnings; `dotnet test` 65/65 ×3. Repro with standalone fake (6210) + gateway (6211): 6 parallel `[[slow:250]]` streams → 3.0 s wall, each recorded 2261 ms (9 events × 250 ms). Published and Debug `Gateway.dll` started from `/` serve `/` and `/accounts` (200). Commit 88180b1 (`GatewayOptions.cs`, `Gateway.csproj`, new `tests/Gateway.Tests/ProxyConcurrencyTests.cs`, `.claude/ledger/fakes.md`).
- **Decisions:** No gateway latency bug: the fake's `[[slow:MS]]` delays *each* SSE event, so 2500 ms × ~12 events ≈ 30 s is correct measurement. Regression test asserts parallel streams aren't serialized and recorded latency ≈ client latency. Web root default now `AppContext.BaseDirectory/wwwroot` (if it has index.html), else content-root `wwwroot`; wwwroot is copied to build output too. Data dir stays `./data` relative to cwd (keeps dev/tests unchanged) — start scripts must set `GATEWAY_DATA_DIR` explicitly.
- **Interface changes:** none.
- **Follow-ups:** README/start scripts: set `GATEWAY_DATA_DIR` to a stable path. Fallback session hash makes non-UUID `metadata.user_id` sessions collapse — real Claude Code uses UUIDs, so e2e should too.
- **Learnings:** Use small `[[slow:N]]` values (per event) in e2e.

## Item 8 — E2E + delivery
- **Status:** done
- **Evidence:** `scripts/e2e.sh`: 77/77 checks pass (main tree and fresh clone). Fresh clone at a84abdc: `dotnet build` 0 warnings/0 errors, `dotnet test` 67/67, `npm run lint` exit 0, `npm run build` ok. Playwright pass on the `--keep` instance: all 6 pages show both providers' traffic, accounts, limits, resets, session moves; only console error is the expected pre-login 401. Running on 6200 from `publish/` (pid in `data/gateway.pid`, PPID 1, own session): `/api/health` 200, `/` serves UI, keyless `/anthropic` and `/openai` → 401 provider-format JSON, 0 accounts linked. Commits 45368b7, fedeb2e, a84abdc.
- **Decisions:** e2e driver is dependency-free Node (`e2e/run.mjs`). Scripts' data dir is `<repo>/data` (override `GATEWAY_DATA_DIR`). Detach uses `nohup perl POSIX::setsid`. Fixes: (1) the selector left accounts that had just returned 429 out of the reset pool, so it redeemed the wrong credit; (2) `count_tokens` without session metadata created phantom sessions and is now stored with a NULL session.
- **Interface changes:** `requests.session_id` is NULL for non-generation rows that have a derived session.
- **Follow-ups:** none.
- **Learnings:** Admin password is in `data/admin-password.txt`. `scripts/stop.sh` and `restart.sh` manage the instance.

## Item 9 — Tokens/second, speed modes, per-agent series
- **Status:** done
- **Evidence:** `dotnet build` 0 warnings/0 errors; `dotnet test` 86/86 (19 new in `ThroughputTests`); `npm run lint` clean; `AUX_PORTS=6300-6359 scripts/e2e.sh` 95/95. Browser pass (Playwright, desktop + 390 px) on the e2e instance: Analytics speed charts, per-agent context chart, session detail, request log; only the expected pre-login 401.
- **Decisions:** Generation time = first output delta → end of stream (JSON: whole latency). Mode = reported value, else requested, else standard. Aggregate = output-token-weighted mean + in-memory p50/p95. Per-agent split = per-request points via a new endpoint; bucket metrics without per-request meaning fall back to `groupBy=agentId`.
- **Interface changes:** migration v5 (5 nullable `requests` columns); metrics `tokensPerSecond[P50|P95]`; groups/filter `speedMode`, `modelMode`; `/api/analytics/agent-series`; new breakdown/summary/request fields; agent labels `session8 · role · instance`. Shapes: `.claude/ledger/analytics.md`.
- **Follow-ups:** Codex subagent threads with their own `session_id` header still form their own sessions (session identification unchanged).
- **Learnings:** Fakes: `[[tps:N]]`, `[[ttft:MS]]`, `[[tool:NAME*N]]`, `Fakes__TokensPerSecond`; e2e sets 1500 tok/s.

## Item 11 — Bridge: catalog, selection, models endpoint, UI
- **Status:** done
- **Evidence:** full `dotnet build` 0 errors; `npm run lint` exit 0; `dotnet test` 113/113 (27 new `BridgeCatalogTests`); browser pass 1440 px light + 390 px dark on 6340/6341 (stopped). Commit d64ea9e on `bridge/catalog`.
- **Decisions:** advertised-id overrides stored only when differing from default; only exposed models validated; `/anthropic/v1/models` runs the existing proxy with captured body (routed, authenticated, recorded), exposed models first; API lists drop non-chat models and dated snapshots; hidden Codex models skipped.
- **Interface changes:** migration 6 (`bridge_models`, `bridge_model_accounts`); `BridgeModelCatalog.ResolveExposed(string?)`, `ListExposed()` → `BridgeModel(AdvertisedId, UpstreamModel, DisplayName, Description, AccountIds, SupportedEfforts, DefaultEffort, CreatedAt)`; public `ProxyHandler.InjectCredentials(req, kind, creds, anthropicBeta?)`, `ProxyHandler.PresentedKey`, `TokenRefresher.SendAuthenticatedAsync(account, build, ct)`; `/api/bridge/*` (shapes in `.claude/ledger/bridge-catalog.md`).
- **Follow-ups:** item 12 must intersect `AccountIds` with eligible accounts (empty = any OpenAI account) and merge the ProxyHandler refactor.
- **Learnings:** `GatewayFactory` has init `DataDir` + `KeepDataDir` for restart tests; fake Codex models endpoint requires `client_version`.

## Item 10 — Bridge: translator
- **Status:** done
- **Evidence:** `dotnet build -p:SkipWebBuild=true` 0 warnings; `dotnet test` 144/144 (58 new: `BridgeTranslationTests`, `BridgeTranslationStreamTests`). Commit 42db28e on `bridge/translator`. Files: `src/Gateway/Bridge/{AnthropicToResponses,ResponsesToAnthropic,AnthropicStreamTranslator,AnthropicErrors,BridgeTokenEstimator,BridgeTranslateOptions}.cs`.
- **Decisions:** billing-header system block dropped (stable cache prefix); reasoning items replayed without `id` (store:false); invalid OpenAI tool names → deterministic hashed short names, mapped back via `ToolNameMap`; one block open at a time; thinking blocks open lazily; bad tool JSON → `{"_raw_arguments":…}`; upstream error messages prefixed "OpenAI upstream error (status):"; usage is `long`.
- **Interface changes:** `AnthropicToResponses.Translate(JsonNode, BridgeTranslateOptions) → BridgeRequest(Body, ClientStream, ToolNameMap, DroppedTools)` + `.UpstreamStream`; throws `BridgeTranslationException` (→ 400). `ResponsesToAnthropic.FromResponseJson(node, advertisedId, toolNameMap?)`. `AnthropicStreamTranslator(advertisedId, toolNameMap?)`: `Feed(span)`/`Complete()` → bytes; props `ResponseId, UpstreamModel, ServiceTier, Usage, StopReason, Started, Finished, Failed, ErrorType, ErrorMessage, HasToolUse`. `AnthropicMessageAggregator(advertisedId, map?)` `.Feed`/`.Complete()→(status, body)`. `AnthropicErrors.FromUpstream/Error/SseEvent/TypeForStatus/StatusForType/TypeForCode`. `BridgeTokenEstimator.Estimate`. `ThinkingEnvelope.Encode/TryDecode`. `BridgeUsage(Input, Cached, Output, Reasoning)`.
- **Follow-ups:** item 12: upstream failure before `translator.Started` → HTTP error, not SSE error; log `DroppedTools`.
- **Learnings:** an `int` literal in a `JsonObject` makes `GetValue<long>()` throw.

## Item 12 — Bridge integration
- **Status:** done
- **Evidence:** merge c74f75d (translator + catalog; worktrees/branches removed); commit 62dae8a. Full `dotnet build` 0 warnings; `dotnet test` 180/180 (9 new `BridgeProxyTests`); `npm run lint` 0. Manual run fakes 6330 / gateway 6331 (stopped): models endpoint lists exposed model, stream incremental, request log provider openai, model gpt-5-codex, requested id, bridge marker, reasoning tokens, 299.6 tok/s.
- **Decisions:** bridging inside `ProxyHandler` loop (shared auth, sessions, selection, refresh, failover, limits, analytics) + `BridgeResponseWriter`; client Anthropic headers not forwarded; pre-stream upstream failure → real HTTP error; bridged count_tokens local estimate, logged without account; agents from the Anthropic request.
- **Interface changes:** migration 7 (`requests.requested_model`, `requests.bridge`); `ProxiedRequestContext.Bridge`; `AccountSelectionRequest.AllowedAccountIds`; `RequestRecord.AccountId` nullable; analytics `requestedModel` group/filter; request log `requestedModel`, `bridge`; fake `[[fail-midstream]]`. Detail: `.claude/ledger/bridge.md`.
- **Follow-ups:** item 13: e2e bridged scenario; README (setup, limitations: server tools dropped, count_tokens estimated). Codex-backend fake now 400s without `store:false`/`instructions`.
- **Learnings:** `StaticWebAssetsEnabled=false` set in Gateway.csproj (stale hashed asset names broke first build after UI change); publish reportedly still copies UI — verify.

## Item 13 — Bridge e2e, README, verification
- **Status:** done
- **Evidence:** `dotnet build` 0 warnings/errors; `dotnet test` 180/180; `npm run lint` 0; `AUX_PORTS=6300-6359 scripts/e2e.sh` 139/139; scratchpad `dotnet publish` ships `wwwroot/index.html`; browser pass (1440 light, 390 dark) of OpenAI models page, request log, analytics group by requested model. Commit 39d3481 (`e2e/run.mjs`, `README.md`).
- **Decisions:** e2e exposes gpt-5.1-codex-max (ChatGPT, default id) and gpt-5-mini (API key, custom id `claude-gw-gpt-5-mini`); every bridged stream strictly validated.
- **Follow-ups:** none.
- **Learnings:** discovery `/v1/models` is recorded as an Anthropic `models` request (model `unknown`); bridged count_tokens rows: provider openai, no account/session.
