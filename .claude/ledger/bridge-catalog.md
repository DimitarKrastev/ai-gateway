# Bridge model catalog, selection, models endpoint, UI (item 11)

## Schema (migration 6)
- `bridge_models(model_id PK, display_name, description, efforts JSON, default_effort, manual, exposed, advertised_id, advertised_name, advertised_description, first_seen, last_seen, updated_at)` — advertised_* store overrides only (NULL = default).
- `bridge_model_accounts(model_id FK cascade, account_id FK accounts cascade, source 'api'|'chatgpt', last_seen, PK(model_id, account_id))`.
- Setting `bridge.lastRefreshAt` (unix ms).

## Service `Gateway.Bridge.BridgeModelCatalog` (singleton, `services.AddBridge()`)
- `BridgeModel? ResolveExposed(string? requestedModel)` — case-insensitive on the advertised id, strips a trailing `[...]` suffix; null if not exposed.
- `IReadOnlyList<BridgeModel> ListExposed()`.
- `record BridgeModel(string AdvertisedId, string UpstreamModel, string DisplayName, string? Description, IReadOnlyList<long> AccountIds, IReadOnlyList<string> SupportedEfforts, string? DefaultEffort, DateTimeOffset CreatedAt)`. `AccountIds` = accounts whose last refresh listed it (may include since-disabled accounts — intersect with eligible ones; empty = manual/unknown → any OpenAI account).
- In-memory snapshot, invalidated on every write/refresh (no DB hit per request).
- `RefreshAsync(ct)` lists every enabled OpenAI account in parallel: API key `GET {OpenAIApi}/v1/models` (filter `IsChatModel`: gpt-/o\d/codex/chatgpt- families minus audio/realtime/tts/transcribe/whisper/image/dall-e/embedding/moderation/search/instruct/davinci/babbage/computer-use/deep-research/sora and dated `-YYYY-MM-DD` snapshots); ChatGPT `GET {ChatGptBackend}/backend-api/codex/models?client_version=0.80.0` with `originator: codex_cli_rs` (skips `visibility` hide/none; takes display_name, description, supported_reasoning_levels[].effort, default_reasoning_level). A failed account keeps its previous rows. Orphans (no account, not manual, not exposed) are deleted; exposed orphans stay with `available:false`.
- `BridgeModelIds`: `DefaultFor(model)` = `anthropic-openai-<model>`, `Validate(id)` (charset `[A-Za-z0-9._:-]`, ≤128, must match `/(claude|anthropic)/i`), `Normalize`.

## Reuse helpers added to the proxy
- `ProxyHandler.InjectCredentials(HttpRequestMessage, string kind, AccountCredentials, string? anthropicBeta = null)` (used by the proxy itself).
- `ProxyHandler.PresentedKey(HttpRequest, provider)` now public.
- `TokenRefresher.SendAuthenticatedAsync(LinkedAccount, Func<AccountCredentials, HttpRequestMessage> build, ct)` — EnsureFresh + one refresh/retry on 401.

## Admin API (cookie auth)
- `GET /api/bridge/models` → `{models: BridgeCatalogEntry[], openaiAccounts:[{id,name,kind,enabled}], anthropicAccounts: <enabled count>, lastRefreshAt, refreshResults: null}`.
  Entry: `modelId, displayName, description, sources[api|chatgpt|manual], accounts[{id,name,kind,enabled,source,lastSeenAt}], efforts[], defaultEffort, manual, firstSeenAt, lastSeenAt, available, exposed, advertisedId, advertisedName, advertisedDescription` (effective) `+ defaultAdvertisedId/Name/Description`.
- `POST /api/bridge/models/refresh` → same view with `refreshResults:[{accountId, accountName, kind, ok, models, error}]`.
- `POST /api/bridge/models {modelId, displayName?, description?, efforts?}` → 201 entry | 400 `{error}`.
- `DELETE /api/bridge/models/{**modelId}` → 204 | 404 | 409 (listed by an account).
- `PUT /api/bridge/selection {models:[{modelId, exposed, advertisedId?, displayName?, description?}]}` → 200 view | 400 `{error, errors:[{modelId, field, message}]}` (all-or-nothing; only exposed models' ids are validated; uniqueness case-insensitive).

## Claude Code endpoints (client-key auth)
- `GET /anthropic/v1/models`: nothing exposed, invalid key, or `after_id`/`before_id` paging → plain proxied call (unchanged). Otherwise exposed models first, then the Anthropic upstream list (proxied via ProxyHandler with the response body captured, so routing/credentials/analytics are the normal ones); no enabled Anthropic account or non-2xx upstream → exposed only. `has_more` from upstream; `last_id` stays the upstream cursor when `has_more`. Exposed entry: `{type:"model", id, display_name, created_at, description}`.
- `GET /anthropic/v1/models/{id}`: exposed id → answered locally; else proxied.

## Fakes
- `FakeOptions.OpenAIApiModels` (ids incl. non-chat ones), `FakeOptions.CodexModels` (`FakeCodexModel(Slug, DisplayName, Description, Efforts, DefaultEffort, Visibility="list", Priority)`; default gpt-5-codex, gpt-5, gpt-5.1-codex-max, hidden codex-auto-review). Codex `/backend-api/codex/models` needs `client_version` (400 otherwise) and returns the codex-rs `ModelInfo` shape.

## UI
- Route `/openai-models`, nav "OpenAI models" (`web/src/pages/OpenAIModels.tsx`, styles `.bridge-*` in index.css). `ApiError.body` now carries the parsed error body.

## Tests
- `GatewayFactory.DataDir` is `init` and `KeepDataDir` lets a second factory reuse the data dir (restart).
