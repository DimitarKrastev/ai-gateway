# AI Coding Gateway — Specification

Build a self-hosted gateway for AI coding agents (Claude Code and OpenAI Codex): a .NET backend and a React web UI.

## What it must do

1. **Transparent gateway.** My Claude Code and Codex connect to the gateway and keep working exactly as if they were talking to their providers directly. When I log into the gateway UI, I see usage statistics.
2. **Linked accounts.** I can add my Claude and OpenAI subscriptions (and API keys) to the gateway as linked accounts for it to use.
3. **Sticky sessions.** An agent session keeps going through the same linked account, so the provider's prompt cache is preserved.
4. **Limits and resets.** For every linked account the gateway shows the relevant limits (session, weekly, and whatever else the provider has) and when they reset. It also shows how many resets are available, and lets me use one.
5. **Maximize utilization (optional).** When enabled, the gateway picks the account whose limits reset the soonest, so quota that would otherwise go unused gets used, moving sessions there (a cache miss is acceptable in that case). When all linked accounts are exhausted, it uses the available reset that is closest to expiring.
6. **Analytics.** Comprehensive statistics and graphs: money cost, tokens (input, output, cache reads, cache writes), context sizes, tool calls, agents (including how many run at the same time), requests and more, over time and broken down by account, session, agent and model.

## Constraints

- Other services run on this machine. Use only the ports given in the environment variables `GATEWAY_PORT` (the gateway), `WEB_PORT` (frontend dev server) and `AUX_PORTS` (a range for anything else you need). Check that a port is free before using it, and never stop a process you did not start.
- Don't run Claude Code or Codex, and don't call any real provider API, while developing or testing. Test against fakes you write.
- Commit your work to the local git repository as you go. The committed repository is the deliverable: a fresh clone of it must build, pass its tests and run.
- When you finish, leave the gateway running on `GATEWAY_PORT`, built from the committed code, serving the UI and accepting requests, and still running after your session ends. The README explains how to stop and restart it.

## Done when

- The backend and frontend build and all tests pass, also from a fresh clone.
- Automated tests cover the important behavior.
- End-to-end runs against your fakes, for both providers, show up correctly in the UI.
- The README explains how to run the gateway, link accounts, and connect Claude Code and Codex.
- The gateway is running as described above.
