import { useMemo, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { ApiError, del, post, put } from '../api'
import { PageHead } from '../components/Layout'
import { Badge, CodeBlock, ConfirmDialog, Empty, ErrorBox, Loading, Panel } from '../components/ui'
import { fmtDateTime, fmtRelative } from '../format'
import { useApi } from '../hooks'
import type { BridgeCatalog, BridgeCatalogEntry, BridgeRefreshResult, BridgeValidationError } from '../types'

/** Claude Code's gateway model discovery keeps only ids matching this. */
const DISCOVERY_FILTER = /(claude|anthropic)/i
const ID_CHARSET = /^[A-Za-z0-9._:-]+$/

type Draft = { exposed: boolean; advertisedId: string; displayName: string }
type FieldErrors = Record<string, { advertisedId?: string; displayName?: string }>

function draftOf(m: BridgeCatalogEntry): Draft {
  return { exposed: m.exposed, advertisedId: m.advertisedId, displayName: m.advertisedName }
}

function effectiveId(m: BridgeCatalogEntry, d: Draft) {
  return d.advertisedId.trim() || m.defaultAdvertisedId
}

function validateId(id: string): string | undefined {
  if (id.length > 128) return 'At most 128 characters.'
  if (!ID_CHARSET.test(id)) return "Use only letters, digits, '.', '_', ':' and '-'."
  if (!DISCOVERY_FILTER.test(id)) return 'Must contain “claude” or “anthropic” — Claude Code ignores other ids.'
  return undefined
}

export default function OpenAIModels() {
  const catalog = useApi<BridgeCatalog>('/api/bridge/models')
  const [refreshResults, setRefreshResults] = useState<BridgeRefreshResult[] | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function refresh() {
    setRefreshing(true)
    setError(null)
    try {
      const view = await post<BridgeCatalog>('/api/bridge/models/refresh')
      setRefreshResults(view.refreshResults)
      catalog.reload()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setRefreshing(false)
    }
  }

  const data = catalog.data
  return (
    <>
      <PageHead title="OpenAI models">
        {data && data.openaiAccounts.length > 0 && (
          <button className="secondary" onClick={() => void refresh()} disabled={refreshing}>
            {refreshing ? 'Refreshing…' : '↻ Refresh from accounts'}
          </button>
        )}
      </PageHead>
      <p className="muted intro">
        Use OpenAI models from Claude Code. The gateway translates Claude Code's Anthropic Messages requests to the OpenAI Responses API and
        routes them through your linked OpenAI accounts (ChatGPT subscription via the Codex backend, or API key). Tick the models to advertise to
        Claude Code.
      </p>
      {error && <ErrorBox error={error} />}
      {!data ? (
        catalog.error ? <ErrorBox error={catalog.error} onRetry={catalog.reload} /> : <Loading />
      ) : (
        <>
          <ModelsPanel
            key={data.models.map((m) => `${m.modelId}:${m.exposed}:${m.advertisedId}:${m.advertisedName}`).join('|')}
            catalog={data}
            refreshResults={refreshResults}
            refreshing={refreshing}
            onRefresh={() => void refresh()}
            onChanged={catalog.reload}
          />
          <SetupPanel catalog={data} />
        </>
      )}
    </>
  )
}

function ModelsPanel({
  catalog,
  refreshResults,
  refreshing,
  onRefresh,
  onChanged,
}: {
  catalog: BridgeCatalog
  refreshResults: BridgeRefreshResult[] | null
  refreshing: boolean
  onRefresh: () => void
  onChanged: () => void
}) {
  const [drafts, setDrafts] = useState<Record<string, Draft>>(() => Object.fromEntries(catalog.models.map((m) => [m.modelId, draftOf(m)])))
  const [serverErrors, setServerErrors] = useState<FieldErrors>({})
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [filter, setFilter] = useState('')
  const [removing, setRemoving] = useState<BridgeCatalogEntry | null>(null)

  const changed = catalog.models.filter((m) => {
    const d = drafts[m.modelId]
    if (!d) return false
    const o = draftOf(m)
    return d.exposed !== o.exposed || effectiveId(m, d) !== m.advertisedId || (d.displayName.trim() || m.defaultAdvertisedName) !== m.advertisedName
  })

  const clientErrors = useMemo(() => {
    const errs: FieldErrors = {}
    const seen = new Map<string, string>()
    for (const m of catalog.models) {
      const d = drafts[m.modelId]
      if (!d?.exposed) continue
      const id = effectiveId(m, d)
      const problem = validateId(id)
      if (problem) errs[m.modelId] = { advertisedId: problem }
      const other = seen.get(id.toLowerCase())
      if (other) {
        errs[m.modelId] = { advertisedId: `Also used by ${other}.` }
        errs[other] = { advertisedId: `Also used by ${m.modelId}.` }
      } else seen.set(id.toLowerCase(), m.modelId)
    }
    return errs
  }, [catalog.models, drafts])

  function update(modelId: string, change: Partial<Draft>) {
    setDrafts((ds) => ({ ...ds, [modelId]: { ...ds[modelId], ...change } }))
    setServerErrors((e) => ({ ...e, [modelId]: {} }))
  }

  async function save() {
    setSaving(true)
    setSaveError(null)
    try {
      await put('/api/bridge/selection', {
        models: changed.map((m) => {
          const d = drafts[m.modelId]
          return { modelId: m.modelId, exposed: d.exposed, advertisedId: d.advertisedId.trim(), displayName: d.displayName.trim() }
        }),
      })
      setServerErrors({})
      onChanged()
    } catch (e) {
      const body = e instanceof ApiError ? (e.body as { errors?: BridgeValidationError[] } | undefined) : undefined
      if (body?.errors?.length) {
        const errs: FieldErrors = {}
        for (const v of body.errors) errs[v.modelId] = { ...errs[v.modelId], [v.field]: v.message }
        setServerErrors(errs)
      }
      setSaveError((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const exposedCount = catalog.models.filter((m) => drafts[m.modelId]?.exposed).length
  const q = filter.trim().toLowerCase()
  const shown = catalog.models.filter(
    (m) => !q || m.modelId.toLowerCase().includes(q) || m.displayName.toLowerCase().includes(q),
  )
  const hasClientErrors = Object.keys(clientErrors).length > 0
  const failed = (refreshResults ?? []).filter((r) => !r.ok)

  return (
    <Panel
      title={
        <>
          Available models <span className="muted">· {exposedCount} exposed</span>
        </>
      }
      actions={
        <>
          {changed.length > 0 && <span className="muted small">{changed.length} unsaved change{changed.length === 1 ? '' : 's'}</span>}
          <button onClick={() => void save()} disabled={saving || changed.length === 0 || hasClientErrors}>
            {saving ? 'Saving…' : 'Save'}
          </button>
        </>
      }
    >
      {catalog.openaiAccounts.length === 0 && (
        <div className="callout warn bridge-callout">
          No OpenAI account is linked. <Link to="/accounts">Link a ChatGPT subscription or an OpenAI API key</Link> to list its models, or add a
          model id manually below.
        </div>
      )}
      {failed.map((r) => (
        <div key={r.accountId} className="callout danger bridge-callout">
          Could not list models for <strong>{r.accountName}</strong>: {r.error}
        </div>
      ))}
      {refreshResults && failed.length === 0 && refreshResults.length > 0 && (
        <p className="muted small bridge-note">
          Listed {refreshResults.map((r) => `${r.models} from ${r.accountName}`).join(', ')}.
        </p>
      )}

      {catalog.models.length === 0 ? (
        <Empty>
          {catalog.openaiAccounts.length > 0 ? (
            <span>
              No models yet.{' '}
              <button className="link" onClick={onRefresh} disabled={refreshing}>
                Refresh from your OpenAI accounts
              </button>{' '}
              or add one manually.
            </span>
          ) : (
            'No models yet.'
          )}
        </Empty>
      ) : (
        <>
          <div className="toolbar bridge-toolbar">
            <input type="search" placeholder="Filter models…" aria-label="Filter models" value={filter} onChange={(e) => setFilter(e.target.value)} />
            <span className="muted small">
              {catalog.lastRefreshAt ? (
                <span title={fmtDateTime(catalog.lastRefreshAt)}>Listed from accounts {fmtRelative(catalog.lastRefreshAt)}</span>
              ) : (
                'Not refreshed from accounts yet'
              )}
            </span>
          </div>
          <div className="table-wrap">
            <table className="table bridge-table">
              <thead>
                <tr>
                  <th>Expose</th>
                  <th>OpenAI model</th>
                  <th>Advertised id</th>
                  <th>Display name in Claude Code</th>
                  <th>Source</th>
                  <th>Reasoning efforts</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {shown.map((m) => {
                  const d = drafts[m.modelId] ?? draftOf(m)
                  const idError = clientErrors[m.modelId]?.advertisedId ?? serverErrors[m.modelId]?.advertisedId
                  const nameError = serverErrors[m.modelId]?.displayName
                  return (
                    <tr key={m.modelId} className={d.exposed ? 'on' : ''}>
                      <td>
                        <input
                          type="checkbox"
                          checked={d.exposed}
                          aria-label={`Expose ${m.modelId} to Claude Code`}
                          onChange={(e) => update(m.modelId, { exposed: e.target.checked })}
                        />
                      </td>
                      <td className="bridge-model">
                        <span className="mono">{m.modelId}</span>
                        {!m.available && (
                          <>
                            {' '}
                            <Badge tone="warn">not listed</Badge>
                          </>
                        )}
                        {m.description && <div className="muted small bridge-desc">{m.description}</div>}
                      </td>
                      <td>
                        <input
                          className={`mono bridge-input ${idError ? 'invalid' : ''}`}
                          value={d.advertisedId}
                          placeholder={m.defaultAdvertisedId}
                          aria-label={`Advertised id for ${m.modelId}`}
                          aria-invalid={idError ? true : undefined}
                          onChange={(e) => update(m.modelId, { advertisedId: e.target.value })}
                        />
                        {idError && <div className="field-error">{idError}</div>}
                      </td>
                      <td>
                        <input
                          className={`bridge-input ${nameError ? 'invalid' : ''}`}
                          value={d.displayName}
                          placeholder={m.defaultAdvertisedName}
                          aria-label={`Display name for ${m.modelId}`}
                          onChange={(e) => update(m.modelId, { displayName: e.target.value })}
                        />
                        {nameError && <div className="field-error">{nameError}</div>}
                      </td>
                      <td>
                        <div className="bridge-sources">
                          {m.sources.map((s) => (
                            <Badge key={s} tone={s === 'manual' ? 'neutral' : 'accent'}>
                              {s === 'api' ? 'API key' : s === 'chatgpt' ? 'ChatGPT' : 'manual'}
                            </Badge>
                          ))}
                        </div>
                        {m.accounts.length > 0 && <div className="muted small">{m.accounts.map((a) => a.name).join(', ')}</div>}
                      </td>
                      <td className="muted small">
                        {m.efforts.length > 0 ? m.efforts.map((e) => (e === m.defaultEffort ? `${e} (default)` : e)).join(', ') : '—'}
                      </td>
                      <td className="row-actions">
                        {m.manual && m.accounts.length === 0 && (
                          <button className="link small danger-text" onClick={() => setRemoving(m)}>
                            Remove
                          </button>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <p className="muted small bridge-note">
            Claude Code's model discovery only shows ids containing “claude” or “anthropic”; the default is <code>anthropic-openai-&lt;model&gt;</code>.
            Leave a field empty to use its default.
          </p>
        </>
      )}
      {saveError && <ErrorBox error={saveError} />}
      <AddManualForm onAdded={onChanged} />
      {removing && (
        <ConfirmDialog
          title="Remove model"
          danger
          confirmLabel="Remove"
          message={
            <p>
              Remove the manually added model <strong className="mono">{removing.modelId}</strong>
              {removing.exposed ? ' and stop advertising it to Claude Code' : ''}?
            </p>
          }
          onConfirm={async () => {
            await del(`/api/bridge/models/${encodeURIComponent(removing.modelId)}`)
            onChanged()
          }}
          onClose={() => setRemoving(null)}
        />
      )}
    </Panel>
  )
}

function AddManualForm({ onAdded }: { onAdded: () => void }) {
  const [modelId, setModelId] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function add(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await post('/api/bridge/models', { modelId: modelId.trim(), displayName: displayName.trim() || null })
      setModelId('')
      setDisplayName('')
      onAdded()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="inline-form bridge-add" onSubmit={add}>
      <label className="grow">
        Add a model manually (OpenAI model id)
        <input className="mono" value={modelId} onChange={(e) => setModelId(e.target.value)} placeholder="e.g. gpt-5.1-codex" required />
      </label>
      <label className="grow">
        Name (optional)
        <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} placeholder="e.g. GPT-5.1 Codex" />
      </label>
      <button type="submit" className="secondary" disabled={busy || !modelId.trim()}>
        {busy ? 'Adding…' : 'Add model'}
      </button>
      {error && <p className="error">{error}</p>}
    </form>
  )
}

function SetupPanel({ catalog }: { catalog: BridgeCatalog }) {
  const origin = window.location.origin
  const exposed = catalog.models.filter((m) => m.exposed)
  const example = exposed[0]
  const exampleId = example?.advertisedId ?? 'anthropic-openai-gpt-5-codex'
  const exampleName = example?.advertisedName ?? 'gpt-5-codex (OpenAI)'
  const exampleDescription = example?.advertisedDescription ?? 'OpenAI gpt-5-codex via the AI Coding Gateway'

  const discoveryShell = `export ANTHROPIC_BASE_URL="${origin}/anthropic"
export ANTHROPIC_AUTH_TOKEN="aigw_…"   # gateway client key (Settings → Client keys)
export CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1
claude   # then pick the model with /model`
  const discoverySettings = `// ~/.claude/settings.json
{
  "env": {
    "ANTHROPIC_BASE_URL": "${origin}/anthropic",
    "ANTHROPIC_AUTH_TOKEN": "aigw_…",
    "CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY": "1"
  }
}`
  const custom = `export ANTHROPIC_BASE_URL="${origin}/anthropic"
export ANTHROPIC_AUTH_TOKEN="aigw_…"
export ANTHROPIC_CUSTOM_MODEL_OPTION="${exampleId}"
export ANTHROPIC_CUSTOM_MODEL_OPTION_NAME="${exampleName}"
export ANTHROPIC_CUSTOM_MODEL_OPTION_DESCRIPTION="${exampleDescription}"`
  const startWith = `claude --model ${exampleId}`
  const background = `# Claude Code's background model (titles, summaries) is a Claude Haiku model by default.
# Without an Anthropic account linked, point it at an exposed OpenAI model too:
export ANTHROPIC_DEFAULT_HAIKU_MODEL="${exampleId}"
export ANTHROPIC_SMALL_FAST_MODEL="${exampleId}"   # older Claude Code versions`

  return (
    <Panel title="Connect Claude Code">
      {exposed.length === 0 && (
        <p className="muted small">No model is exposed yet — the snippets below use an example id. Tick a model above and save.</p>
      )}
      <h4>Option 1 — model discovery (all exposed models in /model)</h4>
      <p className="muted small bridge-note">
        Claude Code asks the gateway for <code>/v1/models</code> and lists the exposed OpenAI models next to the Claude models of your linked
        Anthropic accounts.
      </p>
      <CodeBlock code={discoveryShell} />
      <CodeBlock code={discoverySettings} />
      <h4>Option 2 — a single custom picker entry</h4>
      <p className="muted small bridge-note">Adds one model to /model without discovery (any advertised id works here).</p>
      <CodeBlock code={custom} />
      <p className="muted small bridge-note">Or start a session on it directly:</p>
      <CodeBlock code={startWith} />
      <h4>Background model</h4>
      {catalog.anthropicAccounts === 0 ? (
        <p className="muted small bridge-note">
          <Badge tone="warn">no Anthropic account linked</Badge> Claude Code also sends small background requests to a Claude Haiku model; set:
        </p>
      ) : (
        <p className="muted small bridge-note">
          Optional: with an Anthropic account linked, background requests keep using Claude Haiku. To send them to OpenAI as well:
        </p>
      )}
      <CodeBlock code={background} />
    </Panel>
  )
}
