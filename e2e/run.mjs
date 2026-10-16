#!/usr/bin/env node
// End-to-end scenario: replays Claude Code- and Codex-shaped traffic through a running gateway whose upstreams
// point at the fake providers, then checks that analytics, limits, resets and routing match what the fakes reported.
// No dependencies (Node 20+). Usage: GATEWAY_URL=... FAKES_URL=... GATEWAY_ADMIN_PASSWORD=... node e2e/run.mjs
import { randomUUID, randomBytes } from 'node:crypto';

const GW = process.env.GATEWAY_URL ?? 'http://127.0.0.1:6200';
const FAKES = process.env.FAKES_URL ?? 'http://127.0.0.1:6202';
const PASSWORD = process.env.GATEWAY_ADMIN_PASSWORD;
if (!PASSWORD) { console.error('GATEWAY_ADMIN_PASSWORD is required'); process.exit(2); }

const startedAt = Date.now() - 1000;
const failures = [];
const passes = [];
function check(name, ok, detail = '') {
  (ok ? passes : failures).push(detail ? `${name} — ${detail}` : name);
  console.log(`${ok ? '  ok  ' : ' FAIL '} ${name}${detail ? ` (${detail})` : ''}`);
}
const eq = (name, actual, expected) => check(name, actual === expected, `got ${actual}, expected ${expected}`);

// ---------------------------------------------------------------- HTTP helpers
let cookie = '';
async function admin(method, path, body) {
  const res = await fetch(GW + path, {
    method,
    headers: { 'content-type': 'application/json', cookie },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const setCookie = res.headers.getSetCookie?.() ?? [];
  if (setCookie.length) cookie = setCookie.map((c) => c.split(';')[0]).join('; ');
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}
async function fake(method, path, body) {
  const res = await fetch(FAKES + path, {
    method, headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`fake ${method} ${path} -> ${res.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}

function parseSse(text) {
  const events = [];
  for (const block of text.replace(/\r\n/g, '\n').split('\n\n')) {
    let event = null; const data = [];
    for (const line of block.split('\n')) {
      if (line.startsWith('event:')) event = line.slice(6).trim();
      else if (line.startsWith('data:')) data.push(line.slice(5).trimStart());
    }
    if (!data.length || data.join('') === '[DONE]') continue;
    events.push({ event, data: JSON.parse(data.join('\n')) });
  }
  return events;
}

// Client-side tally of what the fakes reported, per provider, to compare with the gateway's analytics.
const tally = {};
function add(provider, u) {
  const t = (tally[provider] ??= { requests: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, toolCalls: 0 });
  t.requests += 1;
  for (const k of ['inputTokens', 'outputTokens', 'cacheReadTokens', 'cacheWriteTokens', 'toolCalls']) t[k] += u[k] ?? 0;
}

// ---------------------------------------------------------------- Claude Code client
const CC_VERSION = '2.0.14';
const ccUserHash = randomBytes(32).toString('hex');
const ccAccountUuid = randomUUID();
const CC_TOOLS = [
  { name: 'Task', description: 'Launch a new agent to handle complex, multi-step tasks autonomously.', input_schema: { type: 'object', properties: { description: { type: 'string' }, prompt: { type: 'string' }, subagent_type: { type: 'string' } }, required: ['description', 'prompt', 'subagent_type'] } },
  { name: 'Bash', description: 'Executes a given bash command.', input_schema: { type: 'object', properties: { command: { type: 'string' } }, required: ['command'] } },
  { name: 'Read', description: 'Reads a file from the local filesystem.', input_schema: { type: 'object', properties: { file_path: { type: 'string' } }, required: ['file_path'] } },
  { name: 'Grep', description: 'A powerful search tool built on ripgrep.', input_schema: { type: 'object', properties: { pattern: { type: 'string' } }, required: ['pattern'] } },
];
const SUBAGENT_TOOLS = CC_TOOLS.filter((t) => t.name === 'Read' || t.name === 'Grep');
const billingBlock = () => ({ type: 'text', text: `x-anthropic-billing-header: cc_version=${CC_VERSION}.${randomBytes(2).toString('hex')}; cc_entrypoint=cli` });
const MAIN_SYSTEM = (billing) => [
  billing,
  { type: 'text', text: "You are Claude Code, Anthropic's official CLI for Claude.", cache_control: { type: 'ephemeral' } },
  { type: 'text', text: 'You are an interactive CLI tool that helps users with software engineering tasks. '.repeat(40), cache_control: { type: 'ephemeral' } },
];
const SUB_SYSTEM = (billing) => [
  billing,
  { type: 'text', text: "You are Claude Code, Anthropic's official CLI for Claude.", cache_control: { type: 'ephemeral' } },
  { type: 'text', text: 'You are an agent for Claude Code. Given the user\'s message, use the tools available to complete the task. '.repeat(20), cache_control: { type: 'ephemeral' } },
];

class ClaudeSession {
  // The main agent's conversation accumulates across user turns, as in Claude Code; the billing header block is fixed per session.
  constructor(key) { this.key = key; this.id = randomUUID(); this.main = []; this.billing = billingBlock(); }
  headers() {
    return {
      'content-type': 'application/json', accept: 'application/json',
      authorization: `Bearer ${this.key}`,
      'anthropic-version': '2023-06-01',
      'anthropic-beta': 'claude-code-20250219,interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14',
      'anthropic-dangerous-direct-browser-access': 'true',
      'user-agent': `claude-cli/${CC_VERSION} (external, cli)`,
      'x-app': 'cli', 'x-stainless-lang': 'js', 'x-stainless-package-version': '0.60.0',
    };
  }
  metadata() { return { user_id: `user_${ccUserHash}_account_${ccAccountUuid}_session_${this.id}` }; }

  async countTokens(messages) {
    const res = await fetch(`${GW}/anthropic/v1/messages/count_tokens?beta=true`, {
      method: 'POST', headers: this.headers(),
      body: JSON.stringify({ model: 'claude-sonnet-4-5-20250929', messages, tools: CC_TOOLS }),
    });
    const body = await res.json();
    if (res.status !== 200) throw new Error(`count_tokens ${res.status} ${JSON.stringify(body)}`);
    add('anthropic', {});
    return body;
  }

  /** One /v1/messages call; returns {usage, toolUses, text, status}. */
  async message({ system, tools, messages, model = 'claude-sonnet-4-5-20250929', stream = true, maxTokens = 32000 }) {
    const body = { model, max_tokens: maxTokens, stream, system, messages, metadata: this.metadata() };
    if (tools) { body.tools = tools; body.tool_choice = { type: 'auto' }; }
    const res = await fetch(`${GW}/anthropic/v1/messages?beta=true`, { method: 'POST', headers: this.headers(), body: JSON.stringify(body) });
    const text = await res.text();
    if (res.status !== 200) throw new Error(`messages ${res.status} ${text}`);
    let usage = {}; const toolUses = []; let out = '';
    if (stream) {
      const blocks = {};
      for (const { data } of parseSse(text)) {
        if (data.type === 'message_start') usage = { ...data.message.usage };
        else if (data.type === 'message_delta') usage = { ...usage, ...data.usage };
        else if (data.type === 'content_block_start') blocks[data.index] = { ...data.content_block, json: '' };
        else if (data.type === 'content_block_delta') {
          if (data.delta.type === 'text_delta') out += data.delta.text;
          if (data.delta.type === 'input_json_delta') blocks[data.index].json += data.delta.partial_json;
        }
      }
      for (const b of Object.values(blocks)) if (b.type === 'tool_use') toolUses.push({ id: b.id, name: b.name, input: b.json ? JSON.parse(b.json) : {} });
    } else {
      const json = JSON.parse(text);
      usage = json.usage;
      for (const c of json.content) {
        if (c.type === 'tool_use') toolUses.push({ id: c.id, name: c.name, input: c.input });
        if (c.type === 'text') out += c.text;
      }
    }
    add('anthropic', {
      inputTokens: usage.input_tokens, outputTokens: usage.output_tokens,
      cacheReadTokens: usage.cache_read_input_tokens ?? 0, cacheWriteTokens: usage.cache_creation_input_tokens ?? 0,
      toolCalls: toolUses.length,
    });
    return { usage, toolUses, text: out };
  }

  /** A full user turn: main agent delegates to a subagent via Task, the subagent reads a file, both finish. */
  async taskTurn(prompt) {
    const msgs = this.main;
    msgs.push({ role: 'user', content: [{ type: 'text', text: prompt }] });
    await this.countTokens(msgs);
    const r1 = await this.message({ system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs });
    const task = r1.toolUses[0];
    if (!task) throw new Error('main agent did not call a tool');
    // Subagent: own system prompt and tool set, same session.
    const subMsgs = [{ role: 'user', content: [{ type: 'text', text: 'Investigate the repository layout and report back.' }] }];
    const s1 = await this.message({ system: SUB_SYSTEM(this.billing), tools: SUBAGENT_TOOLS, messages: subMsgs });
    const read = s1.toolUses[0];
    subMsgs.push({ role: 'assistant', content: [{ type: 'tool_use', id: read.id, name: read.name, input: read.input }] });
    subMsgs.push({ role: 'user', content: [{ type: 'tool_result', tool_use_id: read.id, content: 'src/\ntests/\nREADME.md' }] });
    const s2 = await this.message({ system: SUB_SYSTEM(this.billing), tools: SUBAGENT_TOOLS, messages: subMsgs });
    // Main agent receives the Task result.
    msgs.push({ role: 'assistant', content: [{ type: 'tool_use', id: task.id, name: task.name, input: task.input }] });
    msgs.push({ role: 'user', content: [{ type: 'tool_result', tool_use_id: task.id, content: s2.text || 'done' }] });
    const r2 = await this.message({ system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs });
    msgs.push({ role: 'assistant', content: [{ type: 'text', text: r2.text || 'done' }] });
  }

  /** Title generation as Claude Code does it: small model, no tools, not streamed. */
  async utility(text) {
    return this.message({
      model: 'claude-haiku-4-5-20251001', stream: false, maxTokens: 512,
      system: [{ type: 'text', text: 'Analyze if this message indicates a new conversation topic. Format your response as a JSON object.' }],
      messages: [{ role: 'user', content: text }],
    });
  }

  /** A simple tool round trip with the main agent (Bash). */
  async bashTurn(prompt) {
    const msgs = this.main;
    msgs.push({ role: 'user', content: [{ type: 'text', text: `${prompt} [[tool:Bash]]` }] });
    const r1 = await this.message({ system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs });
    const call = r1.toolUses[0];
    msgs.push({ role: 'assistant', content: [{ type: 'tool_use', id: call.id, name: call.name, input: call.input }] });
    msgs.push({ role: 'user', content: [{ type: 'tool_result', tool_use_id: call.id, content: 'ok' }] });
    const r2 = await this.message({ system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs });
    msgs.push({ role: 'assistant', content: [{ type: 'text', text: r2.text || 'done' }] });
    return r2;
  }
}

// ---------------------------------------------------------------- Codex client
const CODEX_VERSION = '0.46.0';
const CODEX_INSTRUCTIONS = 'You are Codex, based on GPT-5. You are running as a coding agent in the Codex CLI on a user\'s computer. '.repeat(30);
const CODEX_TOOLS = [
  { type: 'function', name: 'shell', description: 'Runs a shell command and returns its output.', strict: false, parameters: { type: 'object', properties: { command: { type: 'array', items: { type: 'string' } }, workdir: { type: 'string' } }, required: ['command'], additionalProperties: false } },
  { type: 'function', name: 'update_plan', description: 'Updates the task plan.', strict: false, parameters: { type: 'object', properties: { plan: { type: 'array', items: { type: 'object' } } }, required: ['plan'], additionalProperties: false } },
];

class CodexSession {
  constructor(key) { this.key = key; this.id = randomUUID(); this.input = []; }
  headers() {
    return {
      'content-type': 'application/json', accept: 'text/event-stream',
      authorization: `Bearer ${this.key}`,
      'openai-beta': 'responses=experimental',
      originator: 'codex_cli_rs', version: CODEX_VERSION,
      session_id: this.id, conversation_id: this.id,
      'user-agent': `codex_cli_rs/${CODEX_VERSION} (Mac OS 15.6.0; arm64) iTerm.app/3.6.1`,
    };
  }
  async respond() {
    const body = {
      model: 'gpt-5-codex', instructions: CODEX_INSTRUCTIONS, input: this.input, tools: CODEX_TOOLS,
      tool_choice: 'auto', parallel_tool_calls: false, reasoning: { effort: 'medium', summary: 'auto' },
      store: false, stream: true, include: ['reasoning.encrypted_content'], prompt_cache_key: this.id,
    };
    const res = await fetch(`${GW}/openai/v1/responses`, { method: 'POST', headers: this.headers(), body: JSON.stringify(body) });
    const text = await res.text();
    if (res.status !== 200) throw new Error(`responses ${res.status} ${text}`);
    const done = parseSse(text).find((e) => e.data.type === 'response.completed')?.data.response;
    if (!done) throw new Error('no response.completed event');
    const u = done.usage; const cached = u.input_tokens_details?.cached_tokens ?? 0;
    const calls = done.output.filter((o) => o.type.endsWith('_call'));
    add('openai', { inputTokens: u.input_tokens - cached, outputTokens: u.output_tokens, cacheReadTokens: cached, toolCalls: calls.length });
    this.input.push(...done.output.filter((o) => o.type !== 'reasoning'));
    return { usage: u, calls };
  }
  /** User turn: model calls shell, Codex runs it and returns the output, model answers. */
  async turn(prompt) {
    this.input.push({ type: 'message', role: 'user', content: [{ type: 'input_text', text: prompt }] });
    const r = await this.respond();
    for (const c of r.calls) this.input.push({ type: 'function_call_output', call_id: c.call_id, output: JSON.stringify({ output: 'README.md\nsrc\n', metadata: { exit_code: 0 } }) });
    if (r.calls.length) await this.respond();
  }
}

// ---------------------------------------------------------------- scenario
const iso = (ms) => new Date(Date.now() + ms).toISOString();
const H = 3600_000, D = 24 * H;

async function main() {
  console.log(`gateway ${GW}, fakes ${FAKES}`);
  await admin('POST', '/api/auth/login', { password: PASSWORD });
  const key = (await admin('POST', '/api/client-keys', { name: 'e2e' })).key;

  // Fake provider accounts with deterministic limits (no per-request drift) and known reset credits.
  await fake('POST', '/fake-admin/reset');
  const fa = await fake('POST', '/fake-admin/anthropic/accounts', {
    id: 'claude-a', kind: 'oauth', planType: 'max',
    windows: [{ name: 'five_hour', utilization: 10, resetsAt: iso(4 * H), incrementPerRequest: 0 }, { name: 'seven_day', utilization: 30, resetsAt: iso(5 * D), incrementPerRequest: 0 }],
    resets: [{ id: 'rst-a1', expiresAt: iso(2 * D) }], replaceResets: true,
  });
  const fb = await fake('POST', '/fake-admin/anthropic/accounts', {
    id: 'claude-b', kind: 'oauth', planType: 'max',
    windows: [{ name: 'five_hour', utilization: 40, resetsAt: iso(1 * H), incrementPerRequest: 0 }, { name: 'seven_day', utilization: 50, resetsAt: iso(6 * D), incrementPerRequest: 0 }],
    resets: [{ id: 'rst-b1', expiresAt: iso(5 * D) }], replaceResets: true,
  });
  const fk = await fake('POST', '/fake-admin/anthropic/accounts', { id: 'claude-api', kind: 'api_key' });
  const fo = await fake('POST', '/fake-admin/openai/accounts', { id: 'openai-api', kind: 'api_key' });
  const fc = await fake('POST', '/fake-admin/openai/accounts', {
    id: 'chatgpt', kind: 'oauth', planType: 'pro',
    windows: [{ name: 'primary', utilization: 5, incrementPerRequest: 0 }, { name: 'secondary', utilization: 20, incrementPerRequest: 0 }],
  });

  // Link them in the gateway the way a user would (pasting credentials).
  const link = (b) => admin('POST', '/api/accounts', b).then((a) => a.id);
  const claudeCreds = (f) => JSON.stringify({ claudeAiOauth: { accessToken: f.accessToken, refreshToken: f.refreshToken, expiresAt: Date.parse(f.accessTokenExpiresAt ?? iso(8 * H)), scopes: ['user:inference', 'user:profile'], subscriptionType: 'max' } });
  const A = await link({ name: 'Claude Max A', credentialsJson: claudeCreds(fa), priority: 10 });
  const B = await link({ name: 'Claude Max B', credentialsJson: claudeCreds(fb), priority: 100 });
  const K = await link({ name: 'Anthropic API key', credentialsJson: fk.apiKey, priority: 100 });
  const O = await link({ name: 'OpenAI API key', kind: 'openai_api_key', apiKey: fo.apiKey });
  const C = await link({ name: 'ChatGPT Pro', credentialsJson: JSON.stringify({ OPENAI_API_KEY: null, tokens: { id_token: 'e2e', access_token: fc.accessToken, refresh_token: fc.refreshToken, account_id: fc.chatGptAccountId }, last_refresh: new Date().toISOString() }) });
  const accountsList = await admin('GET', '/api/accounts');
  eq('5 linked accounts', accountsList.length, 5);
  check('linked account kinds', ['anthropic_oauth', 'anthropic_api_key', 'openai_api_key', 'chatgpt_oauth'].every((k) => accountsList.some((a) => a.kind === k)));
  check('accounts API never returns secrets', !JSON.stringify(accountsList).includes(fa.accessToken) && !JSON.stringify(accountsList).includes(fk.apiKey));

  // Phase 1 — maximize off. S1 lands on Claude A (priority 10); after re-prioritizing, S2 lands on the API key while S1 stays put.
  console.log('phase 1: sticky sessions, both providers');
  const s1 = new ClaudeSession(key);
  await s1.utility('Refactor the session store to use SQLite');
  await s1.taskTurn('Find where sessions are stored and summarize it.');
  await admin('PATCH', `/api/accounts/${K}`, { priority: 1 });
  const s2 = new ClaudeSession(key);
  await s2.bashTurn('Run the tests');
  await s1.bashTurn('Now run the build');

  // Codex: C1 through the OpenAI API key (ChatGPT disabled), C2 through the ChatGPT subscription (API key disabled).
  await admin('PATCH', `/api/accounts/${C}`, { enabled: false });
  const c1 = new CodexSession(key);
  await c1.turn('List the files in this repo');
  await admin('PATCH', `/api/accounts/${C}`, { enabled: true });
  await admin('PATCH', `/api/accounts/${O}`, { enabled: false });
  const c2 = new CodexSession(key);
  await c2.turn('Explain the build script');
  await admin('PATCH', `/api/accounts/${O}`, { enabled: true });
  // Concurrent agents: both providers in parallel, streams slowed so they overlap.
  await Promise.all([c1.turn('Run the linter [[slow:20]]'), c2.turn('Show git status [[slow:20]]'), s2.bashTurn('Check formatting [[slow:20]]')]);

  // Phase 2 — maximize utilization on.
  console.log('phase 2: maximize utilization, exhaustion, automatic reset');
  await admin('PUT', '/api/settings', { maximizeUtilization: true, autoUseResets: true });
  const s3 = new ClaudeSession(key);
  await s3.bashTurn('Start a new feature');              // -> Claude B (resets soonest)
  await s1.bashTurn('Continue');                          // S1 moves A -> B (B resets >= 30 min sooner)
  await fake('PATCH', '/fake-admin/anthropic/accounts/claude-b', { windows: [{ name: 'five_hour', utilization: 100 }] });
  await s3.bashTurn('Keep going');                        // B 429 -> failover to A
  await fake('PATCH', '/fake-admin/anthropic/accounts/claude-a', { windows: [{ name: 'five_hour', utilization: 100 }] });
  await s3.bashTurn('Almost done');                       // A 429 -> all subscriptions exhausted -> redeem rst-a1 (expires first)
  await admin('PUT', '/api/settings', { maximizeUtilization: false });

  // Manual reset use from the UI API (ChatGPT account, provider credit).
  const cResets = await admin('GET', `/api/accounts/${C}/resets`);
  check('ChatGPT account lists provider reset credits', cResets.filter((r) => r.available).length >= 1, `${cResets.length} credits`);
  const used = await admin('POST', `/api/accounts/${C}/resets/${cResets[0].id}/use`);
  check('manual reset use succeeds', used.ok === true);

  // ------------------------------------------------------------ assertions
  console.log('checks');
  const from = `from=${startedAt}`;
  for (const provider of ['anthropic', 'openai']) {
    const s = await admin('GET', `/api/analytics/summary?${from}&provider=${provider}`);
    const t = tally[provider];
    for (const k of ['requests', 'inputTokens', 'outputTokens', 'cacheReadTokens', 'cacheWriteTokens', 'toolCalls']) eq(`${provider} ${k} matches fake-reported`, s[k], t[k]);
    check(`${provider} cost > 0`, s.costUsd > 0, `$${s.costUsd?.toFixed(4)}`);
    check(`${provider} cache reads observed`, s.cacheReadTokens > 0, `${s.cacheReadTokens}`);
    check(`${provider} context size recorded`, s.maxContextTokens > 0, `max ${s.maxContextTokens}`);
  }
  const all = await admin('GET', `/api/analytics/summary?${from}`);
  check('cache writes observed (anthropic)', tally.anthropic.cacheWriteTokens > 0, `${tally.anthropic.cacheWriteTokens}`);
  check('peak concurrent requests >= 2', all.peakConcurrentRequests >= 2, `${all.peakConcurrentRequests}`);
  check('peak concurrent agents >= 2', all.peakConcurrentAgents >= 2, `${all.peakConcurrentAgents}`);

  const agents = (await admin('GET', `/api/analytics/breakdown?${from}&by=agent`)).rows.map((r) => r.key);
  for (const a of ['claude-code:main', 'claude-code:subagent', 'claude-code:utility', 'codex:main']) check(`agent kind ${a} present`, agents.includes(a), agents.join(', '));
  const models = (await admin('GET', `/api/analytics/breakdown?${from}&by=model`)).rows.map((r) => r.key);
  for (const m of ['claude-sonnet-4-5-20250929', 'claude-haiku-4-5-20251001', 'gpt-5-codex']) check(`model ${m} present`, models.includes(m));
  const tools = (await admin('GET', `/api/analytics/breakdown?${from}&by=tool`)).rows.map((r) => r.key);
  for (const tl of ['Task', 'Read', 'Bash', 'shell']) check(`tool ${tl} counted`, tools.includes(tl), tools.join(', '));
  const byAccount = (await admin('GET', `/api/analytics/breakdown?${from}&by=account`)).rows;
  for (const id of [A, B, K, O, C]) check(`account ${id} served traffic`, byAccount.some((r) => String(r.key) === String(id) && r.requests > 0));

  const sessions = (await admin('GET', `/api/analytics/sessions?${from}&limit=200`)).items;
  const sess = (s) => sessions.find((x) => x.sessionId === s.id);
  for (const [n, s] of Object.entries({ s1, s2, s3, c1, c2 })) check(`session ${n} recorded`, !!sess(s));
  eq('S2 sticky on one account', sess(s2)?.accountsUsed, 1);
  eq('C1 sticky on one account', sess(c1)?.accountsUsed, 1);
  eq('C2 sticky on one account', sess(c2)?.accountsUsed, 1);
  eq('S1 used two accounts (moved by maximize)', sess(s1)?.accountsUsed, 2);
  eq('S1 agents: main + subagent + utility', sess(s1)?.agents, 3);

  const fakeReqs = { anthropic: await fake('GET', '/fake-admin/anthropic/requests'), openai: await fake('GET', '/fake-admin/openai/requests') };
  const acctOf = (provider, sessionId, pathPrefix) => [...new Set(fakeReqs[provider].filter((r) => r.path.startsWith(pathPrefix) && r.body.includes(sessionId) && r.statusCode === 200).map((r) => r.accountId))];
  check('S2 served by the Anthropic API key', JSON.stringify(acctOf('anthropic', s2.id, '/v1/messages')) === '["claude-api"]', JSON.stringify(acctOf('anthropic', s2.id, '/v1/messages')));
  check('C1 served by the OpenAI API key (/v1/responses)', JSON.stringify(acctOf('openai', c1.id, '/v1/responses')) === '["openai-api"]');
  check('C2 served by ChatGPT (/backend-api/codex/responses)', JSON.stringify(acctOf('openai', c2.id, '/backend-api/codex/responses')) === '["chatgpt"]');
  const allFake = [...fakeReqs.anthropic, ...fakeReqs.openai];
  check('gateway client key never reaches providers', !allFake.some((r) => JSON.stringify(r.headers).includes(key)));
  check('Claude subscription requests carry OAuth beta', fakeReqs.anthropic.filter((r) => r.accountId === 'claude-a' && r.path === '/v1/messages').every((r) => (r.headers['anthropic-beta'] ?? '').includes('oauth-2025-04-20')));
  check('Claude Code headers preserved', fakeReqs.anthropic.some((r) => r.headers['user-agent']?.startsWith('claude-cli/') && r.headers['x-app'] === 'cli'));
  check('ChatGPT requests carry account id', fakeReqs.openai.filter((r) => r.path.startsWith('/backend-api/codex')).every((r) => r.headers['chatgpt-account-id'] === fc.chatGptAccountId));

  const events = await admin('GET', '/api/routing/events?limit=200');
  for (const reason of ['maximize', 'exhausted', 'reset_used']) check(`routing event '${reason}'`, events.some((e) => e.reason === reason), events.map((e) => e.reason).join(', '));
  check('S1 moved to Claude B by maximize', events.some((e) => e.reason === 'maximize' && e.sessionId === s1.id && e.toAccountId === B));
  const fakeA = await fake('GET', '/fake-admin/anthropic/accounts/claude-a');
  const fakeB = await fake('GET', '/fake-admin/anthropic/accounts/claude-b');
  eq('reset closest to expiring (Claude A) redeemed at provider', fakeA.redeemedResets, 1);
  eq('later-expiring reset (Claude B) untouched', fakeB.redeemedResets, 0);
  const aResets = await admin('GET', `/api/accounts/${A}/resets?includeUsed=true`);
  check('gateway shows auto-used reset', aResets.some((r) => r.id && r.usedBy === 'auto'), JSON.stringify(aResets.map((r) => [r.source, r.usedBy])));
  eq('ChatGPT reset redeemed at provider', (await fake('GET', '/fake-admin/openai/accounts/chatgpt')).redeemedResets, 1);

  const limits = Object.fromEntries((await admin('GET', '/api/accounts')).map((a) => [a.id, a.limits]));
  const keys = (id) => (limits[id]?.windows ?? []).map((w) => w.key);
  check('Claude A limits: session + weekly', ['five_hour', 'seven_day'].every((k) => keys(A).includes(k)), keys(A).join(','));
  check('Claude B exhausted', limits[B]?.exhausted === true || (limits[B]?.windows ?? []).some((w) => w.status === 'exhausted'));
  check('Anthropic API key limits: requests', keys(K).includes('requests'), keys(K).join(','));
  check('OpenAI API key limits: requests', keys(O).includes('requests'), keys(O).join(','));
  check('ChatGPT limits: primary + secondary', ['primary', 'secondary'].every((k) => keys(C).includes(k)), keys(C).join(','));
  check('resets summary on accounts', typeof limits[B]?.resets?.available === 'number', JSON.stringify(limits[B]?.resets));

  const log = (await admin('GET', '/api/analytics/requests?limit=500')).items;
  check('request log has both clients', ['claude-code', 'codex'].every((c) => log.some((r) => r.client === c)));
  check('gateway-retried attempts visible in log', log.some((r) => r.retried));

  console.log(`\n${passes.length} passed, ${failures.length} failed`);
  console.log(JSON.stringify({ tally, sessions: { s1: s1.id, s2: s2.id, s3: s3.id, c1: c1.id, c2: c2.id } }));
  if (failures.length) { console.error('FAILURES:\n' + failures.join('\n')); process.exit(1); }
}

main().catch((e) => { console.error(e); process.exit(1); });
