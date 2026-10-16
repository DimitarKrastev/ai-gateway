# Ledger

## Item 1 — Scaffold
- **Status:** done
- **Evidence:** `dotnet build` 0 warnings/0 errors; `dotnet test` 6/6 passed; `npm run build` + `npm run lint` (oxlint) clean; fresh `git clone` → build + test pass; published build run briefly on 6202: health 200, `/` and `/accounts` serve SPA, `/api/auth/me` 401, `/openai/*` 404. Commit 63628ce.
- **Decisions:** Vite builds into `src/Gateway/wwwroot` (gitignored); MSBuild targets in `Gateway.csproj` run `npm ci` (if no node_modules) and incremental `npm run build` on every build, `-p:SkipWebBuild=true` skips; built assets added to Content for publish. Bind 127.0.0.1 by default (`GATEWAY_BIND`). Pinned Sqlite 10.0.3 + `SQLitePCLRaw.bundle_e_sqlite3` 2.1.14 (fixes NU1903). Data Protection keys in `<data>/keys`.
- **Interface changes:** `GatewayOptions` (Port, BindAddress, DataDir, WebRoot, AdminPassword; `Gateway:*` config → env → default). `Database` singleton: `Open()`, `Migrate()`, append to `Database.Migrations` (v1 = `settings` table), `Get/SetSetting`. `UseApiLoginRequirement` gates all `/api/*` except health and auth (allowlist in `AuthEndpoints.AnonymousApiPaths`). SPA fallback excludes `/api`, `/anthropic`, `/openai`.
- **Follow-ups:** README sections marked "To be completed". `Gateway.Fakes` is a placeholder Program.
- **Learnings:** Runtime installed is 10.0.3 — pin Microsoft.* packages to 10.0.3. Tests: `GatewayFactory` (temp DataDir, `AdminPassword`, `CreateLoggedInClientAsync()`).
