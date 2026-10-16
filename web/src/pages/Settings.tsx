import { useState, type FormEvent } from 'react'
import { del, post, put } from '../api'
import { PageHead } from '../components/Layout'
import { Async, CodeBlock, ConfirmDialog, Empty, ErrorBox, Modal, Panel, Toggle } from '../components/ui'
import { fmtDateTime, fmtRelative } from '../format'
import { useApi } from '../hooks'
import type { ClientKey, Settings as SettingsT } from '../types'

export default function Settings() {
  return (
    <>
      <PageHead title="Settings" />
      <RoutingPanel />
      <ClientKeysPanel />
      <PasswordPanel />
    </>
  )
}

type PasswordInfo = { source: 'configured' | 'generated' | 'custom'; changeable: boolean; minLength: number }

function PasswordPanel() {
  const info = useApi<PasswordInfo>('/api/auth/password')
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const [busy, setBusy] = useState(false)
  const min = info.data?.minLength ?? 12

  async function change(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setDone(false)
    if (next.length < min) return setError(`The new password must be at least ${min} characters.`)
    if (next !== confirm) return setError('The new passwords do not match.')
    setBusy(true)
    try {
      await post('/api/auth/change-password', { currentPassword: current, newPassword: next })
      setCurrent('')
      setNext('')
      setConfirm('')
      setDone(true)
      info.reload()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Panel title="Admin password">
      <Async state={info}>
        {(p) =>
          !p.changeable ? (
            <p className="muted small">
              The password is set by <code>GATEWAY_ADMIN_PASSWORD</code> (or <code>Gateway:AdminPassword</code>). Change it there and restart the
              gateway.
            </p>
          ) : (
            <>
              <p className="muted small">
                {p.source === 'generated'
                  ? 'You are using the generated password from admin-password.txt. After a change only a salted hash is kept and that file is deleted.'
                  : 'Only a salted hash of the password is stored.'}{' '}
                Changing it signs out every other browser session.
              </p>
              <form className="inline-form" onSubmit={change}>
                <label className="grow">
                  Current password
                  <input type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
                </label>
                <label className="grow">
                  New password
                  <input
                    type="password"
                    autoComplete="new-password"
                    minLength={min}
                    value={next}
                    onChange={(e) => setNext(e.target.value)}
                    placeholder={`at least ${min} characters`}
                    required
                  />
                </label>
                <label className="grow">
                  Repeat new password
                  <input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
                </label>
                <button type="submit" disabled={busy}>
                  {busy ? 'Changing…' : 'Change password'}
                </button>
              </form>
              {error && <ErrorBox error={error} />}
              {done && <p className="small ok-text">Password changed. Other sessions have been signed out.</p>}
            </>
          )
        }
      </Async>
    </Panel>
  )
}

function RoutingPanel() {
  const settings = useApi<SettingsT>('/api/settings')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  async function update(change: Partial<SettingsT>) {
    setSaving(true)
    setError(null)
    try {
      await put('/api/settings', change)
      settings.reload()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Panel title="Routing">
      <Async state={settings}>
        {(s) => (
          <div className="settings">
            <div className="setting">
              <Toggle checked={s.maximizeUtilization} disabled={saving} onChange={(v) => update({ maximizeUtilization: v })} label="Maximize utilization" />
              <p className="muted small">
                Off: each agent session stays on its account (preserving the provider's prompt cache) and only moves when that account is exhausted
                or disabled; new sessions go to the least-utilized account. On: requests go to the subscription account whose quota window resets
                soonest, so quota that would otherwise expire unused gets used. Sessions move when another account resets at least 30 minutes
                sooner — the next request on the new account is a prompt-cache miss.
              </p>
            </div>
            <div className="setting">
              <Toggle checked={s.autoUseResets} disabled={saving} onChange={(v) => update({ autoUseResets: v })} label="Automatically use resets" />
              <p className="muted small">
                When every account for a provider is exhausted, redeem the available provider reset that expires soonest and route there. Manually
                recorded resets are never used automatically.
                {s.autoUseResetsIsDefault && ' (Currently following the “Maximize utilization” setting.)'}
              </p>
            </div>
          </div>
        )}
      </Async>
      {error && <ErrorBox error={error} />}
    </Panel>
  )
}

function snippets(key: string) {
  const origin = window.location.origin
  return {
    claude: `# Claude Code (shell profile or before running \`claude\`)
export ANTHROPIC_BASE_URL="${origin}/anthropic"
export ANTHROPIC_AUTH_TOKEN="${key}"`,
    claudeSettings: `// or ~/.claude/settings.json
{
  "env": {
    "ANTHROPIC_BASE_URL": "${origin}/anthropic",
    "ANTHROPIC_AUTH_TOKEN": "${key}"
  }
}`,
    codex: `# ~/.codex/config.toml
model_provider = "gateway"

[model_providers.gateway]
name = "AI Coding Gateway"
base_url = "${origin}/openai/v1"
wire_api = "responses"
env_key = "AI_GATEWAY_KEY"`,
    codexEnv: `export AI_GATEWAY_KEY="${key}"`,
  }
}

function ClientKeysPanel() {
  const keys = useApi<ClientKey[]>('/api/client-keys')
  const [name, setName] = useState('')
  const [created, setCreated] = useState<ClientKey | null>(null)
  const [revoking, setRevoking] = useState<ClientKey | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function create(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const k = await post<ClientKey>('/api/client-keys', { name: name.trim() || 'Client key' })
      setCreated(k)
      setName('')
      keys.reload()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const s = snippets(created?.key ?? 'aigw_…')

  return (
    <Panel title="Client keys">
      <p className="muted small">
        Claude Code and Codex authenticate to the gateway with a client key. The gateway replaces it with a linked account's credentials before
        forwarding.
      </p>
      <form className="inline-form" onSubmit={create}>
        <label className="grow">
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. laptop – Claude Code" />
        </label>
        <button type="submit" disabled={busy}>
          {busy ? 'Creating…' : 'Create key'}
        </button>
      </form>
      {error && <ErrorBox error={error} />}
      <Async state={keys} empty={(k) => (k.length === 0 ? <Empty>No client keys yet — create one to connect your agents.</Empty> : null)}>
        {(list) => (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Key</th>
                  <th>Created</th>
                  <th>Last used</th>
                  <th>Status</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {list.map((k) => (
                  <tr key={k.id} className={k.revokedAt ? 'dim' : ''}>
                    <td>{k.name}</td>
                    <td className="mono">{k.prefix}…</td>
                    <td>{fmtDateTime(k.createdAt)}</td>
                    <td>{k.lastUsedAt ? fmtRelative(k.lastUsedAt) : 'never'}</td>
                    <td>{k.revokedAt ? `revoked ${fmtRelative(k.revokedAt)}` : 'active'}</td>
                    <td className="row-actions">
                      {!k.revokedAt && (
                        <button className="link small danger-text" onClick={() => setRevoking(k)}>
                          Revoke
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Async>

      <details className="snippets">
        <summary>Connection instructions</summary>
        <h4>Claude Code</h4>
        <CodeBlock code={s.claude} />
        <CodeBlock code={s.claudeSettings} />
        <h4>Codex</h4>
        <CodeBlock code={s.codex} />
        <CodeBlock code={s.codexEnv} />
      </details>

      {created && (
        <Modal
          title="Client key created"
          onClose={() => setCreated(null)}
          footer={<button onClick={() => setCreated(null)}>Done</button>}
        >
          <p>
            Copy this key now — it is shown only once.
          </p>
          <CodeBlock code={created.key ?? ''} />
          <h4>Claude Code</h4>
          <CodeBlock code={s.claude} />
          <h4>Codex</h4>
          <CodeBlock code={s.codex} />
          <CodeBlock code={s.codexEnv} />
        </Modal>
      )}
      {revoking && (
        <ConfirmDialog
          title="Revoke client key"
          danger
          confirmLabel="Revoke"
          message={
            <p>
              Revoke <strong>{revoking.name}</strong>? Agents using it will get 401 errors immediately.
            </p>
          }
          onConfirm={async () => {
            await del(`/api/client-keys/${revoking.id}`)
            keys.reload()
          }}
          onClose={() => setRevoking(null)}
        />
      )}
    </Panel>
  )
}
