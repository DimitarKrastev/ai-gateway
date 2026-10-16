# Bridge integration (item 12)

## Request path
- `ProxyHandler.HandleAsync`: POST `/anthropic/v1/messages` (any query) whose `model` resolves via
  `BridgeModelCatalog.ResolveExposed` is bridged; `/anthropic/v1/messages/count_tokens` for such a model is answered
  locally (`BridgeTokenEstimator`) and logged directly through `UsageStore` (provider openai, no account, endpoint
  `count_tokens`, `requested_model`, `bridge`). Everything else is unchanged pass-through.
- Session id: `SessionIdentifier` on the Anthropic request (Claude Code metadata/header). Selection: `IAccountSelector`
  with provider `openai`, upstream path `v1/responses`, `AllowedAccountIds = BridgeModel.AccountIds` (intersected with
  enabled+eligible in `PolicyAccountSelector`; empty or no overlap = any OpenAI account). Binding key is
  (`openai`, Claude Code session id), separate from the session's Anthropic binding.
- Per attempt: translated with `CodexBackend = account is ChatGPT`; upstream URI `router.BuildUri(kind, "v1/responses")`
  (ChatGPT → `/backend-api/codex/responses`). Headers: only content-type/accept/user-agent + credentials; ChatGPT adds
  `OpenAI-Beta: responses=experimental`, `originator: codex_cli_rs`, `session_id`. Client Anthropic headers are not forwarded.
  401 → token refresh + retry, 429 → failover (max 3 attempts) as for pass-through.
- `BridgeResponseWriter`: non-2xx → `AnthropicErrors.FromUpstream` JSON (status kept, `retry-after` passed, upstream
  `x-request-id` as `x-upstream-request-id`); upstream JSON → `ResponsesToAnthropic`; SSE → `AnthropicStreamTranslator`
  per chunk (headers sent with the first event; failure before `message_start` → HTTP error); SSE for a non-stream client →
  `AnthropicMessageAggregator`. Upstream bytes go to observers first. Broken upstream mid-stream → SSE `error` event.
- Translation failure → 400 invalid_request_error (not recorded). No OpenAI account → 503 overloaded_error naming the model.
  Dropped server tools logged at Information.

## Analytics
- Migration 7: `requests.requested_model`, `requests.bridge` (`anthropic->openai`), NULL for pass-through.
- `ProxiedRequestContext.Bridge` (`BridgedRequest(RequestedModel, UpstreamModel, ClientBody)`): `Provider`=openai,
  `RequestBody`=translated, `Path`=client path (`v1/messages`), `RequestHeaders`=client's. `RequestInfo.Parse` uses endpoint
  `responses` (usage/tps/reasoning/cached/tool calls from the upstream stream) and the client body for tools/system/first
  user (Claude Code agent heuristics). `RequestRecord.AccountId` is now nullable.
- `requestedModel` group/breakdown/filter (`COALESCE(requested_model, model, 'unknown')`); request log items carry
  `requestedModel`, `bridge`; live items `RequestedModel`. UI: request log "model via <advertised id>", Analytics group,
  breakdown and filter chip "Requested model".

## Fakes
- `/backend-api/codex/responses` → 400 `{"detail":…}` unless stream:true, store:false, non-empty instructions, and no
  max_output_tokens/temperature/top_p.
- `reasoning.summary` (≠ none) → reasoning item first (summary deltas; `encrypted_content` when include has
  `reasoning.encrypted_content`). Incoming reasoning items need `summary` (array) and an encrypted_content the fake issued
  (else 400 `invalid_encrypted_content`).
- `[[fail-midstream]]` → `response.failed` after the first output delta.

## Build
- `StaticWebAssetsEnabled=false` in Gateway.csproj: the SDK's asset discovery remembered the previous build's hashed UI
  files and the first `dotnet build` after a UI change failed. The UI is served from wwwroot by PhysicalFileProvider; publish
  still copies wwwroot.
