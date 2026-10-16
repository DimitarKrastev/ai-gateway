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
