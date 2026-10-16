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

/**
 * Validates an Anthropic Messages SSE stream the way a strict client would: event names match data types, message_start
 * first, content blocks opened/closed one at a time with consecutive indices, delta types match their block, tool_use
 * partial_json concatenates to a JSON object, thinking blocks carry a signature, one message_delta, message_stop last.
 * Returns a list of problems (empty = valid).
 */
function validateAnthropicStream(events, model) {
  const problems = [];
  const p = (m) => problems.push(m);
  if (events[0]?.data.type !== 'message_start') p(`first event ${events[0]?.data.type}`);
  if (events.at(-1)?.data.type !== 'message_stop') p(`last event ${events.at(-1)?.data.type}`);
  let next = 0; let open = null; let deltas = 0;
  for (const { event, data } of events) {
    if (event && event !== data.type) p(`event ${event} carries ${data.type}`);
    switch (data.type) {
      case 'message_start': {
        const m = data.message;
        if (!(m?.type === 'message' && m.role === 'assistant' && Array.isArray(m.content) && m.content.length === 0 && m.id?.startsWith('msg_'))) p('bad message_start');
        if (model && m?.model !== model) p(`message_start model ${m?.model}`);
        if (typeof m?.usage?.input_tokens !== 'number') p('message_start without usage');
        break;
      }
      case 'ping': break;
      case 'content_block_start':
        if (open) p(`block ${data.index} started while ${open.index} open`);
        if (data.index !== next) p(`block index ${data.index}, expected ${next}`);
        open = { index: data.index, type: data.content_block.type, json: '', signature: data.content_block.signature ?? '' };
        if (open.type === 'tool_use' && !(data.content_block.id && data.content_block.name)) p('tool_use without id/name');
        next++;
        break;
      case 'content_block_delta': {
        if (!open || open.index !== data.index) { p(`delta for closed block ${data.index}`); break; }
        const want = { text: ['text_delta'], thinking: ['thinking_delta', 'signature_delta'], tool_use: ['input_json_delta'] }[open.type] ?? [];
        if (!want.includes(data.delta.type)) p(`${data.delta.type} in ${open.type} block`);
        if (data.delta.type === 'input_json_delta') open.json += data.delta.partial_json;
        if (data.delta.type === 'signature_delta') open.signature += data.delta.signature;
        break;
      }
      case 'content_block_stop':
        if (!open || open.index !== data.index) { p(`stop for closed block ${data.index}`); break; }
        if (open.type === 'tool_use') {
          try { const v = JSON.parse(open.json || '{}'); if (typeof v !== 'object' || Array.isArray(v) || v === null) p('tool input not an object'); }
          catch { p(`tool input does not parse: ${open.json}`); }
        }
        if (open.type === 'thinking' && !open.signature) p('thinking block without signature');
        open = null;
        break;
      case 'message_delta':
        deltas++;
        if (open) p('message_delta with an open block');
        if (!['end_turn', 'tool_use', 'max_tokens', 'stop_sequence', 'refusal', 'pause_turn'].includes(data.delta?.stop_reason)) p(`stop_reason ${data.delta?.stop_reason}`);
        if (typeof data.usage?.output_tokens !== 'number') p('message_delta without output_tokens');
        break;
      case 'message_stop': break;
      case 'error': p(`error event ${JSON.stringify(data.error)}`); break;
      default: p(`unknown event ${data.type}`);
    }
  }
  if (deltas !== 1) p(`${deltas} message_delta events`);
  return problems;
}

// Bridged models (advertised id -> OpenAI model), filled in once exposed; requests for them are tallied as OpenAI traffic.
const BRIDGED = new Map();
const bridgeStreams = { count: 0, problems: [] };

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

  async countTokens(messages, model = 'claude-sonnet-4-5-20250929') {
    const res = await fetch(`${GW}/anthropic/v1/messages/count_tokens?beta=true`, {
      method: 'POST', headers: this.headers(),
      body: JSON.stringify({ model, messages, tools: CC_TOOLS }),
    });
    const body = await res.json();
    if (res.status !== 200) throw new Error(`count_tokens ${res.status} ${JSON.stringify(body)}`);
    add(BRIDGED.has(model) ? 'openai' : 'anthropic', {});
    if (BRIDGED.has(model)) add('bridge', {});
    return body;
  }

  /**
   * One /v1/messages call; returns {usage, toolUses, text, content, stopReason}. `content` is the assistant message as
   * Claude Code would echo it back (thinking blocks with their signatures, text, tool_use).
   */
  async message({ system, tools, messages, model = 'claude-sonnet-4-5-20250929', stream = true, maxTokens = 32000, speed, thinking }) {
    const body = { model, max_tokens: maxTokens, stream, system, messages, metadata: this.metadata() };
    if (speed) body.speed = speed; // Claude Code fast mode
    if (thinking) body.thinking = thinking;
    if (tools) { body.tools = tools; body.tool_choice = { type: 'auto' }; }
    const res = await fetch(`${GW}/anthropic/v1/messages?beta=true`, { method: 'POST', headers: this.headers(), body: JSON.stringify(body) });
    const text = await res.text();
    if (res.status !== 200) throw new Error(`messages ${res.status} ${text}`);
    const bridged = BRIDGED.has(model);
    let usage = {}; const toolUses = []; let out = ''; let content = []; let stopReason = null;
    if (stream) {
      const events = parseSse(text);
      if (bridged) {
        bridgeStreams.count++;
        for (const pr of validateAnthropicStream(events, model)) bridgeStreams.problems.push(`${model}: ${pr}`);
      }
      const blocks = {};
      for (const { data } of events) {
        if (data.type === 'message_start') usage = { ...data.message.usage };
        else if (data.type === 'message_delta') { usage = { ...usage, ...data.usage }; stopReason = data.delta.stop_reason; }
        else if (data.type === 'content_block_start') blocks[data.index] = { ...data.content_block, json: '' };
        else if (data.type === 'content_block_delta') {
          const b = blocks[data.index];
          if (data.delta.type === 'text_delta') { out += data.delta.text; b.text = (b.text ?? '') + data.delta.text; }
          if (data.delta.type === 'input_json_delta') b.json += data.delta.partial_json;
          if (data.delta.type === 'thinking_delta') b.thinking = (b.thinking ?? '') + data.delta.thinking;
          if (data.delta.type === 'signature_delta') b.signature = (b.signature ?? '') + data.delta.signature;
        }
      }
      for (const b of Object.keys(blocks).sort((x, y) => x - y).map((i) => blocks[i])) {
        if (b.type === 'tool_use') {
          const input = b.json ? JSON.parse(b.json) : {};
          toolUses.push({ id: b.id, name: b.name, input });
          content.push({ type: 'tool_use', id: b.id, name: b.name, input });
        } else if (b.type === 'thinking') content.push({ type: 'thinking', thinking: b.thinking ?? '', signature: b.signature ?? '' });
        else if (b.type === 'text') content.push({ type: 'text', text: b.text ?? '' });
      }
    } else {
      const json = JSON.parse(text);
      usage = json.usage; stopReason = json.stop_reason;
      if (bridged && !(json.type === 'message' && json.role === 'assistant' && json.model === model && Array.isArray(json.content) && json.stop_reason)) {
        bridgeStreams.problems.push(`${model}: bad non-stream message ${text.slice(0, 200)}`);
      }
      content = json.content;
      for (const c of json.content) {
        if (c.type === 'tool_use') toolUses.push({ id: c.id, name: c.name, input: c.input });
        if (c.type === 'text') out += c.text;
      }
    }
    const u = {
      inputTokens: usage.input_tokens, outputTokens: usage.output_tokens,
      cacheReadTokens: usage.cache_read_input_tokens ?? 0, cacheWriteTokens: usage.cache_creation_input_tokens ?? 0,
      toolCalls: toolUses.length,
    };
    add(bridged ? 'openai' : 'anthropic', u);
    if (bridged) add('bridge', u);
    return { usage, toolUses, text: out, content, stopReason };
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

  /**
   * Main agent fans out to several subagents at once (parallel Task calls). Each subagent runs its own loop of three
   * requests (Read, then Grep, then its answer), so its context grows independently of the others.
   */
  async parallelTaskTurn(prompt, areas) {
    const msgs = this.main;
    msgs.push({ role: 'user', content: [{ type: 'text', text: `${prompt} [[tool:Task*${areas.length}]]` }] });
    const r1 = await this.message({ system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs });
    if (r1.toolUses.length !== areas.length) throw new Error(`expected ${areas.length} Task calls, got ${r1.toolUses.length}`);
    const results = await Promise.all(areas.map(async (area) => {
      const sub = [{ role: 'user', content: [{ type: 'text', text: `Audit the ${area} module and report findings. [[tool:Read]]` }] }];
      for (const [tool, output] of [['Read', `// ${area}.ts\n` + `export function ${area}() { /* ... */ }\n`.repeat(60)], ['Grep', `${area}.ts:12: TODO\n`.repeat(40)]]) {
        const r = await this.message({ system: SUB_SYSTEM(this.billing), tools: SUBAGENT_TOOLS, messages: sub });
        const call = r.toolUses[0];
        if (!call) throw new Error(`subagent ${area} did not call ${tool}`);
        sub.push({ role: 'assistant', content: [{ type: 'tool_use', id: call.id, name: call.name, input: call.input }] });
        sub.push({ role: 'user', content: [{ type: 'tool_result', tool_use_id: call.id, content: output }, ...(tool === 'Read' ? [{ type: 'text', text: '[[tool:Grep]]' }] : [])] });
      }
      return (await this.message({ system: SUB_SYSTEM(this.billing), tools: SUBAGENT_TOOLS, messages: sub })).text || 'done';
    }));
    msgs.push({ role: 'assistant', content: r1.toolUses.map((t) => ({ type: 'tool_use', id: t.id, name: t.name, input: t.input })) });
    msgs.push({ role: 'user', content: r1.toolUses.map((t, i) => ({ type: 'tool_result', tool_use_id: t.id, content: results[i] })) });
    const r2 = await this.message({ system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs });
    msgs.push({ role: 'assistant', content: [{ type: 'text', text: r2.text || 'done' }] });
  }

  /** A long plain answer from the main agent, in standard or fast mode (`speed: "fast"`), to compare output speed. */
  async speedTurn(prompt, speed) {
    const msgs = this.main;
    msgs.push({ role: 'user', content: [{ type: 'text', text: `${prompt} [[no-tool]] [[out:200]]` }] });
    const r = await this.message({ system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs, speed });
    msgs.push({ role: 'assistant', content: [{ type: 'text', text: r.text || 'done' }] });
    return r;
  }

  /** Title generation as Claude Code does it: small model, no tools, not streamed. */
  async utility(text, model = 'claude-haiku-4-5-20251001') {
    return this.message({
      model, stream: false, maxTokens: 512,
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

  /**
   * A Claude Code turn on a bridged OpenAI model with extended thinking: the main agent calls Task, a subagent reads a
   * file and answers, the main agent gets the result. Assistant messages are echoed back exactly as received (thinking
   * blocks with their signatures first), so the OpenAI reasoning must round-trip through the gateway.
   */
  async bridgedTaskTurn(model, prompt) {
    const thinking = { type: 'enabled', budget_tokens: 8000 };
    const msgs = this.main;
    msgs.push({ role: 'user', content: [{ type: 'text', text: `${prompt} [[tool:Task]]` }] });
    await this.countTokens(msgs, model);
    const r1 = await this.message({ model, system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs, thinking });
    const task = r1.toolUses[0];
    if (r1.stopReason !== 'tool_use' || task?.name !== 'Task') throw new Error(`bridged main agent: ${r1.stopReason} ${JSON.stringify(r1.content)}`);
    const sub = [{ role: 'user', content: [{ type: 'text', text: 'Investigate the repository layout and report back. [[tool:Read]]' }] }];
    const s1 = await this.message({ model, system: SUB_SYSTEM(this.billing), tools: SUBAGENT_TOOLS, messages: sub, thinking });
    const read = s1.toolUses[0];
    if (read?.name !== 'Read') throw new Error(`bridged subagent did not call Read: ${JSON.stringify(s1.content)}`);
    sub.push({ role: 'assistant', content: s1.content });
    sub.push({ role: 'user', content: [{ type: 'tool_result', tool_use_id: read.id, content: 'src/\ntests/\nREADME.md' }] });
    const s2 = await this.message({ model, system: SUB_SYSTEM(this.billing), tools: SUBAGENT_TOOLS, messages: sub, thinking });
    msgs.push({ role: 'assistant', content: r1.content });
    msgs.push({ role: 'user', content: [{ type: 'tool_result', tool_use_id: task.id, content: s2.text || 'done' }] });
    const r2 = await this.message({ model, system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs, thinking });
    msgs.push({ role: 'assistant', content: r2.content });
    return { r1, s1, s2, r2 };
  }

  /** A Bash round trip on a bridged model: streamed tool call, then the tool result answered without streaming. */
  async bridgedBashTurn(model, prompt) {
    const msgs = this.main;
    msgs.push({ role: 'user', content: [{ type: 'text', text: `${prompt} [[tool:Bash]]` }] });
    const r1 = await this.message({ model, system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs });
    const call = r1.toolUses[0];
    if (r1.stopReason !== 'tool_use' || call?.name !== 'Bash') throw new Error(`bridged Bash turn: ${JSON.stringify(r1.content)}`);
    msgs.push({ role: 'assistant', content: r1.content });
    msgs.push({ role: 'user', content: [{ type: 'tool_result', tool_use_id: call.id, content: 'ok' }] });
    const r2 = await this.message({ model, system: MAIN_SYSTEM(this.billing), tools: CC_TOOLS, messages: msgs, stream: false });
    msgs.push({ role: 'assistant', content: r2.content });
    return { r1, r2 };
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
  async respond(serviceTier) {
    const body = {
      model: 'gpt-5-codex', instructions: CODEX_INSTRUCTIONS, input: this.input, tools: CODEX_TOOLS,
      tool_choice: 'auto', parallel_tool_calls: false, reasoning: { effort: 'medium', summary: 'auto' },
      store: false, stream: true, include: ['reasoning.encrypted_content'], prompt_cache_key: this.id,
    };
    if (serviceTier) body.service_tier = serviceTier; // Codex fast mode sends e.g. "priority"
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
  /** A long plain answer, in standard or fast mode, to compare output speed. */
  async speedTurn(prompt, serviceTier) {
    this.input.push({ type: 'message', role: 'user', content: [{ type: 'input_text', text: `${prompt} [[no-tool]] [[out:200]]` }] });
    await this.respond(serviceTier);
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

  // Phase 0 — OpenAI models for Claude Code: list what the linked OpenAI accounts offer, expose two models (one from the
  // ChatGPT subscription only, one from the API key only, the latter with a custom advertised id and name).
  console.log('phase 0: OpenAI models catalog, exposure, discovery endpoint');
  const catalog = await admin('POST', '/api/bridge/models/refresh');
  check('catalog refresh succeeds for both OpenAI accounts', catalog.refreshResults?.length === 2 && catalog.refreshResults.every((r) => r.ok),
    JSON.stringify(catalog.refreshResults?.map((r) => [r.kind, r.ok, r.models, r.error])));
  const entry = (id) => catalog.models.find((m) => m.modelId === id);
  check('catalog lists ChatGPT and API models', !!entry('gpt-5.1-codex-max') && !!entry('gpt-5-mini') && !!entry('gpt-5-codex'), catalog.models.map((m) => m.modelId).join(', '));
  check('catalog filters non-chat models', !catalog.models.some((m) => /embedding|tts|whisper|dall-e|realtime|transcribe|moderation/.test(m.modelId)));
  check('catalog knows which account lists a model', JSON.stringify(entry('gpt-5.1-codex-max')?.sources) === '["chatgpt"]' && JSON.stringify(entry('gpt-5-mini')?.sources) === '["api"]',
    `${JSON.stringify(entry('gpt-5.1-codex-max')?.sources)} ${JSON.stringify(entry('gpt-5-mini')?.sources)}`);
  eq('default advertised id scheme', entry('gpt-5.1-codex-max')?.defaultAdvertisedId, 'anthropic-openai-gpt-5.1-codex-max');
  const badSel = await fetch(`${GW}/api/bridge/selection`, {
    method: 'PUT', headers: { 'content-type': 'application/json', cookie },
    body: JSON.stringify({ models: [{ modelId: 'gpt-5-mini', exposed: true, advertisedId: 'gpt-5-mini' }] }),
  });
  eq('advertised id without claude/anthropic is rejected', badSel.status, 400);
  const BRIDGE_MAX = 'anthropic-openai-gpt-5.1-codex-max';
  const BRIDGE_MINI = 'claude-gw-gpt-5-mini';
  await admin('PUT', '/api/bridge/selection', { models: [
    { modelId: 'gpt-5.1-codex-max', exposed: true },
    { modelId: 'gpt-5-mini', exposed: true, advertisedId: BRIDGE_MINI, displayName: 'GPT-5 mini via gateway' },
  ] });
  BRIDGED.set(BRIDGE_MAX, 'gpt-5.1-codex-max');
  BRIDGED.set(BRIDGE_MINI, 'gpt-5-mini');
  const saved = await admin('GET', '/api/bridge/models');
  check('selection persisted', saved.models.filter((m) => m.exposed).map((m) => m.advertisedId).sort().join(',') === [BRIDGE_MAX, BRIDGE_MINI].sort().join(','));

  // Claude Code gateway model discovery (CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1): GET /v1/models?limit=1000.
  const discovery = new ClaudeSession(key);
  const mres = await fetch(`${GW}/anthropic/v1/models?limit=1000`, { headers: { ...discovery.headers(), accept: 'application/json' } });
  const mbody = await mres.json();
  eq('discovery /v1/models status', mres.status, 200);
  const mids = (mbody.data ?? []).map((m) => m.id);
  add('anthropic', {}); // the real Claude list is fetched through an Anthropic account and logged as a models call
  check('discovery lists the exposed OpenAI models', mids.includes(BRIDGE_MAX) && mids.includes(BRIDGE_MINI), mids.join(', '));
  check('discovery keeps the real Claude models', mids.some((id) => id.startsWith('claude-sonnet')), mids.join(', '));
  check('every discovered id passes Claude Code\'s /(claude|anthropic)/i filter', mids.length > 0 && mids.every((id) => /(claude|anthropic)/i.test(id)));
  const mini = mbody.data?.find((m) => m.id === BRIDGE_MINI);
  check('custom display name advertised', mini?.display_name === 'GPT-5 mini via gateway' && mini?.type === 'model', JSON.stringify(mini));
  const noKey = await fetch(`${GW}/anthropic/v1/models?limit=1000`, { headers: { 'anthropic-version': '2023-06-01' } });
  const noKeyBody = await noKey.json();
  check('discovery requires a client key (Anthropic error format)', noKey.status === 401 && noKeyBody.type === 'error' && !!noKeyBody.error?.type, `${noKey.status} ${JSON.stringify(noKeyBody)}`);

  // Phase 1 — maximize off. S1 lands on Claude A (priority 10); after re-prioritizing, S2 lands on the API key while S1 stays put.
  console.log('phase 1: sticky sessions, both providers');
  const s1 = new ClaudeSession(key);
  await s1.utility('Refactor the session store to use SQLite');
  await s1.taskTurn('Find where sessions are stored and summarize it.');
  await s1.parallelTaskTurn('Audit the payment flow.', ['auth', 'billing', 'search']);
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
  // Speed modes: the same models in standard and fast mode (the fakes generate 2.5× faster in fast mode).
  for (let i = 0; i < 2; i++) {
    await s2.speedTurn(`Explain the design, part ${i}`);
    await s2.speedTurn(`Explain the design quickly, part ${i}`, 'fast');
    await c1.speedTurn(`Summarize the module, part ${i}`);
    await c1.speedTurn(`Summarize the module quickly, part ${i}`, 'priority');
  }
  await c2.speedTurn('Summarize the README quickly', 'fast');
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

  // Phase 3 — Claude Code on exposed OpenAI models (Anthropic Messages in, OpenAI Responses upstream).
  console.log('phase 3: Claude Code on OpenAI models through the bridge');
  const b1 = new ClaudeSession(key); // ChatGPT-only model: Codex backend, streamed upstream even for non-stream calls
  await b1.utility('Add retries to the HTTP client', BRIDGE_MAX);
  const bt = await b1.bridgedTaskTurn(BRIDGE_MAX, 'Map out the HTTP client and its callers.');
  check('bridged thinking block carries the bridge signature', bt.r1.content[0]?.type === 'thinking' && bt.r1.content[0].signature.startsWith('bridge1:'),
    JSON.stringify(bt.r1.content.map((c) => c.type)));
  check('bridged follow-up ends the turn', bt.r2.stopReason === 'end_turn', bt.r2.stopReason);
  check('bridged usage reports cache reads on later turns', bt.r2.usage.cache_read_input_tokens > 0, JSON.stringify(bt.r2.usage));
  const b2 = new ClaudeSession(key); // API-key-only model with a custom advertised id
  const bb = await b2.bridgedBashTurn(BRIDGE_MINI, 'Run the unit tests');
  check('bridged non-stream reply is an Anthropic message', bb.r2.stopReason === 'end_turn' && bb.r2.content.some((c) => c.type === 'text'), JSON.stringify(bb.r2.content));
  const errRes = await fetch(`${GW}/anthropic/v1/messages?beta=true`, {
    method: 'POST', headers: b2.headers(),
    body: JSON.stringify({ model: BRIDGE_MINI, max_tokens: 1000, stream: true, metadata: b2.metadata(), messages: [{ role: 'user', content: 'Explain [[error:400]]' }] }),
  });
  const errBody = await errRes.json().catch(() => null);
  check('bridged upstream error comes back in Anthropic error format', errRes.status === 400 && errBody?.type === 'error' && errBody.error?.type === 'invalid_request_error',
    `${errRes.status} ${JSON.stringify(errBody)}`);
  add('openai', {});
  add('bridge', {});
  check(`bridged Anthropic streams are valid (${bridgeStreams.count} streams)`, bridgeStreams.count === 5 && bridgeStreams.problems.length === 0, bridgeStreams.problems.slice(0, 5).join('; '));

  // ------------------------------------------------------------ assertions
  console.log('checks');
  const from = `from=${startedAt}`;
  for (const provider of ['anthropic', 'openai']) {
    const s = await admin('GET', `/api/analytics/summary?${from}&provider=${provider}`);
    const t = tally[provider];
    for (const k of ['requests', 'inputTokens', 'outputTokens', 'cacheReadTokens', 'cacheWriteTokens', 'toolCalls']) eq(`${provider} ${k} matches fake-reported`, s[k], t[k]);
    eq(`${provider} sessions`, s.sessions, provider === 'anthropic' ? 3 : 4); // openai: 2 Codex + 2 bridged Claude Code
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
  for (const [n, s] of Object.entries({ s1, s2, s3, c1, c2, b1, b2 })) check(`session ${n} recorded`, !!sess(s));
  eq('S2 sticky on one account', sess(s2)?.accountsUsed, 1);
  eq('C1 sticky on one account', sess(c1)?.accountsUsed, 1);
  eq('C2 sticky on one account', sess(c2)?.accountsUsed, 1);
  eq('S1 used two accounts (moved by maximize)', sess(s1)?.accountsUsed, 2);
  eq('S1 agents: main + 4 subagents + utility', sess(s1)?.agents, 6);

  // Per-agent series: one line per agent instance in S1, starting at its first request.
  const ctx = await admin('GET', `/api/analytics/agent-series?${from}&sessionId=${s1.id}&metric=contextMax&limit=20`);
  const byRole = (r) => ctx.agents.filter((a) => a.role === r);
  eq('S1 per-agent context series: one per agent instance', ctx.agents.length, 6);
  eq('S1 per-agent series: 1 main', byRole('main').length, 1);
  eq('S1 per-agent series: 4 subagents', byRole('subagent').length, 4);
  const mainFirst = byRole('main')[0]?.firstSeen ?? Infinity;
  const parallel = byRole('subagent').filter((a) => a.points.length === 3);
  eq('parallel subagents each have their own 3-request series', parallel.length, 3);
  check('subagents appear after the main agent', byRole('subagent').every((a) => a.firstSeen > mainFirst));
  check('each parallel subagent context grows', parallel.every((a) => a.points[0][1] < a.points[1][1] && a.points[1][1] < a.points[2][1]),
    parallel.map((a) => a.points.map((p) => p[1]).join('>')).join(' | '));
  const overlapping = parallel.every((a) => parallel.every((b) => a === b || (a.firstSeen <= b.lastSeen && b.firstSeen <= a.lastSeen)));
  check('parallel subagents overlap in time', overlapping);
  check('agent labels name session and role', ctx.agents.every((a) => a.label.startsWith(s1.id.slice(0, 8)) && a.label.includes(a.role)), ctx.agents.map((a) => a.label).join(', '));
  const cumReq = await admin('GET', `/api/analytics/agent-series?${from}&sessionId=${s1.id}&metric=requests`);
  check('per-agent request counts are running totals', cumReq.cumulative && cumReq.agents.every((a) => a.points.at(-1)[1] === a.requests));

  // Output speed and speed modes.
  const modeRows = (await admin('GET', `/api/analytics/breakdown?${from}&by=modelMode`)).rows;
  const tpsOf = (k) => modeRows.find((r) => r.key === k)?.tokensPerSecond ?? null;
  for (const model of ['claude-sonnet-4-5-20250929', 'gpt-5-codex']) {
    const std = tpsOf(`${model}|standard`), fast = tpsOf(`${model}|fast`);
    check(`${model}: tokens/s measured in standard and fast mode`, std > 0 && fast > 0, `standard ${std}, fast ${fast}`);
    check(`${model}: fast mode is faster than standard`, fast > 1.5 * std, `standard ${std}, fast ${fast}`);
  }
  const speedRows = (await admin('GET', `/api/analytics/breakdown?${from}&by=speedMode`)).rows;
  check('speed modes recorded', ['standard', 'fast'].every((m) => speedRows.some((r) => r.key === m)), speedRows.map((r) => `${r.key}:${r.requests}`).join(', '));
  eq('fast-mode requests counted', speedRows.find((r) => r.key === 'fast')?.requests, 5);
  const tpsTs = await admin('GET', `/api/analytics/timeseries?${from}&metric=tokensPerSecondP95&groupBy=modelMode`);
  check('tokens/s p95 timeseries by model and mode', tpsTs.series.some((x) => x.id.endsWith('|fast') && x.total > 0), tpsTs.series.map((x) => x.label).join(', '));
  const sum = await admin('GET', `/api/analytics/summary?${from}`);
  check('summary tokens/s with p50 <= p95', sum.tokensPerSecond > 0 && sum.tokensPerSecondP50 <= sum.tokensPerSecondP95, `${sum.tokensPerSecond} (${sum.tokensPerSecondP50}/${sum.tokensPerSecondP95})`);

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

  // Bridged traffic: recorded as OpenAI traffic with both the advertised id and the actual model.
  const bridgeLog = (await admin('GET', `/api/analytics/requests?limit=500`)).items.filter((r) => r.bridge && !r.retried && r.startedAt >= startedAt);
  check('bridged rows are marked', bridgeLog.length > 0 && bridgeLog.every((r) => r.bridge === 'anthropic->openai'), `${bridgeLog.length} rows`);
  check('bridged rows: provider openai, actual model, advertised id', bridgeLog.every((r) => r.provider === 'openai' && BRIDGED.get(r.requestedModel) === r.model),
    [...new Set(bridgeLog.map((r) => `${r.provider}:${r.model}<-${r.requestedModel}`))].join(', '));
  const bsum = (k) => bridgeLog.reduce((a, r) => a + (r[k] ?? 0), 0);
  for (const k of ['inputTokens', 'outputTokens', 'cacheReadTokens', 'cacheWriteTokens', 'toolCalls']) eq(`bridged ${k} match what the client received`, bsum(k), tally.bridge[k]);
  eq('bridged requests counted', bridgeLog.length, tally.bridge.requests);
  const bGen = bridgeLog.filter((r) => r.status === 200 && r.endpoint !== 'count_tokens');
  check('bridged streamed rows have tokens/second', bGen.filter((r) => r.stream && r.outputTokens > 0).every((r) => r.tokensPerSecond > 0), bGen.map((r) => r.tokensPerSecond).join(','));
  check('bridged reasoning tokens recorded', bGen.some((r) => r.requestedModel === BRIDGE_MAX && r.reasoningTokens > 0));
  check('bridged count_tokens logged', bridgeLog.some((r) => r.endpoint === 'count_tokens' && r.requestedModel === BRIDGE_MAX));
  const reqModels = (await admin('GET', `/api/analytics/breakdown?${from}&by=requestedModel`)).rows.map((r) => r.key);
  check('breakdown by requested model shows advertised ids', reqModels.includes(BRIDGE_MAX) && reqModels.includes(BRIDGE_MINI), reqModels.join(', '));
  const bAgents = (await admin('GET', `/api/analytics/breakdown?${from}&by=agent&sessionId=${b1.id}`)).rows.map((r) => r.key).sort();
  eq('bridged session agents: main, subagent, utility', bAgents.join(','), 'claude-code:main,claude-code:subagent,claude-code:utility');
  eq('bridged session B1 sticky on one account', sess(b1)?.accountsUsed, 1);
  eq('bridged session B1 agents', sess(b1)?.agents, 3);
  eq('bridged session B2 sticky on one account', sess(b2)?.accountsUsed, 1);
  const bAgentSeries = await admin('GET', `/api/analytics/agent-series?${from}&sessionId=${b1.id}&metric=contextMax&limit=20`);
  check('bridged per-agent series', bAgentSeries.agents.length === 3 && bAgentSeries.agents.some((a) => a.role === 'subagent' && a.points.length === 2),
    bAgentSeries.agents.map((a) => `${a.role}:${a.points.length}`).join(', '));
  check('B1 served by ChatGPT (/backend-api/codex/responses)', JSON.stringify(acctOf('openai', b1.id, '/backend-api/codex/responses')) === '["chatgpt"]',
    JSON.stringify(acctOf('openai', b1.id, '/backend-api/codex/responses')));
  check('B2 served by the OpenAI API key (/v1/responses)', JSON.stringify(acctOf('openai', b2.id, '/v1/responses')) === '["openai-api"]',
    JSON.stringify(acctOf('openai', b2.id, '/v1/responses')));
  const b1Up = fakeReqs.openai.filter((r) => r.path === '/backend-api/codex/responses' && r.body.includes(b1.id)).map((r) => JSON.parse(r.body));
  check('Codex backend requests: stream, store:false, instructions, no max_output_tokens', b1Up.length >= 5 && b1Up.every((b) => b.stream === true && b.store === false && b.instructions?.length > 0 && !('max_output_tokens' in b) && b.model === 'gpt-5.1-codex-max'),
    `${b1Up.length} requests`);
  check('echoed thinking replayed as encrypted reasoning', b1Up.some((b) => b.input.some((i) => i.type === 'reasoning' && i.encrypted_content)));
  check('tool results sent as function_call_output', b1Up.some((b) => b.input.some((i) => i.type === 'function_call_output')));
  const b2Up = fakeReqs.openai.filter((r) => r.path === '/v1/responses' && r.body.includes(b2.id)).map((r) => JSON.parse(r.body));
  check('API requests carry max_output_tokens and the real model', b2Up.length >= 2 && b2Up.every((b) => b.max_output_tokens > 0 && b.model === 'gpt-5-mini'), `${b2Up.length} requests`);

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
  check('request log keeps raw speed values', log.some((r) => r.requestedSpeed === 'fast' && r.reportedSpeed === 'fast' && r.speedMode === 'fast')
    && log.some((r) => r.requestedSpeed === 'priority' && r.reportedSpeed === 'priority' && r.speedMode === 'fast'));
  check('gateway-retried attempts visible in log', log.some((r) => r.retried));

  console.log(`\n${passes.length} passed, ${failures.length} failed`);
  console.log(JSON.stringify({ tally, sessions: { s1: s1.id, s2: s2.id, s3: s3.id, c1: c1.id, c2: c2.id, b1: b1.id, b2: b2.id } }));
  if (failures.length) { console.error('FAILURES:\n' + failures.join('\n')); process.exit(1); }
}

main().catch((e) => { console.error(e); process.exit(1); });
