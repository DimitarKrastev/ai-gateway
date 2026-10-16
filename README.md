# AI Coding Gateway

A self-hosted gateway for AI coding agents (Claude Code and OpenAI Codex): a .NET 10 backend
(`src/Gateway`) that transparently proxies agent traffic through linked provider accounts, and a
React web UI (`web/`) for accounts, limits and usage analytics.

## Repository layout

| Path | What |
| --- | --- |
| `src/Gateway` | ASP.NET Core gateway: proxy, accounts, analytics API, serves the built UI from `wwwroot/` |
| `src/Gateway.Fakes` | Fake Anthropic / OpenAI servers used by tests and end-to-end runs |
| `tests/Gateway.Tests` | xUnit tests (in-memory `WebApplicationFactory`) |
| `web` | Vite + React + TypeScript UI |

## Building and testing

Requirements: .NET SDK 10.0.103+ (see `global.json`), Node.js 20+ and npm.

```sh
dotnet build          # also runs `npm ci` (first time) and `npm run build` in web/, output -> src/Gateway/wwwroot
dotnet test
```

The web build is incremental (skipped when `wwwroot/index.html` is newer than every web source).
Pass `-p:SkipWebBuild=true` to skip it. Frontend-only: `cd web && npm run build` / `npm run lint`.

UI development with hot reload: run the gateway, then `cd web && npm run dev` (Vite on `WEB_PORT`,
default 6201, proxying `/api`, `/anthropic`, `/openai` to the gateway on `GATEWAY_PORT`).

## Running

_To be completed._

### Configuration

| Setting | Env var | Config key | Default |
| --- | --- | --- | --- |
| Listen port | `GATEWAY_PORT` | `Gateway:Port` | `6200` |
| Bind address | `GATEWAY_BIND` | `Gateway:BindAddress` | `127.0.0.1` |
| Data directory (SQLite DB, keys, generated password) | `GATEWAY_DATA_DIR` | `Gateway:DataDir` | `./data` (relative to the working directory) |
| UI files | `GATEWAY_WEB_ROOT` | `Gateway:WebRoot` | `wwwroot` next to the app |
| Admin password | `GATEWAY_ADMIN_PASSWORD` | `Gateway:AdminPassword` | generated on first start into `<data dir>/admin-password.txt` |

### Logging in

Open `http://localhost:6200/` and sign in with the admin password.

## Linking accounts

_To be completed._

## Connecting Claude Code

_To be completed._

## Connecting Codex

_To be completed._

## Stopping and restarting

_To be completed._

## Assumptions

- The gateway is single-user: one admin password protects the UI and its `/api` endpoints.
- By default it listens on `127.0.0.1` only; set `GATEWAY_BIND=0.0.0.0` to expose it on the network.
