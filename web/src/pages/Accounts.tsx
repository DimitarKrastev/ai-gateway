import { useState, type FormEvent } from 'react'
import { del, patch, post } from '../api'
import { PageHead } from '../components/Layout'
import { AccountStatus, Async, Badge, ConfirmDialog, Empty, ErrorBox, LimitBar, Modal, Toggle } from '../components/ui'
import { fmtDateTime, fmtRelative, kindLabel, providerLabel, toMs } from '../format'
import { useApi, useNow } from '../hooks'
import type { Account, AccountKind, AccountLimits, ResetCredit } from '../types'

export default function Accounts() {
  const accounts = useApi<Account[]>('/api/accounts', 30_000, ['accounts', 'limits'])
  const [linking, setLinking] = useState(false)
  const now = useNow(1000)

  return (
    <>
      <PageHead title="Linked accounts">
        <button onClick={() => setLinking(true)}>+ Link account</button>
      </PageHead>
      <p className="muted intro">
        Subscriptions and API keys the gateway uses to serve your agents. Sessions stick to one account so the provider's prompt cache is
        preserved; lower priority numbers are preferred for new sessions.
      </p>
      <Async
        state={accounts}
        empty={(a) =>
          a.length === 0 ? (
            <Empty>
              No accounts linked yet. <button className="link" onClick={() => setLinking(true)}>Link your first account</button>
            </Empty>
          ) : null
        }
      >
        {(list) => (
          <div className="accounts">
            {list.map((a) => (
              <AccountCard key={a.id} account={a} now={now} onChanged={accounts.reload} />
            ))}
          </div>
        )}
      </Async>
      {linking && (
        <AccountForm
          onClose={() => setLinking(false)}
          onSaved={() => {
            setLinking(false)
            accounts.reload()
          }}
        />
      )}
    </>
  )
}

function AccountCard({ account: a, now, onChanged }: { account: Account; now: number; onChanged: () => void }) {
  const [limits, setLimits] = useState<AccountLimits | null>(null)
  const [editing, setEditing] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [showResets, setShowResets] = useState(false)
  const l = limits ?? a.limits
  const expires = toMs(a.accessTokenExpiresAt)

  async function refresh() {
    setRefreshing(true)
    setError(null)
    try {
      setLimits(await post<AccountLimits>(`/api/accounts/${a.id}/limits/refresh`))
      onChanged()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setRefreshing(false)
    }
  }

  async function toggleEnabled(enabled: boolean) {
    setError(null)
    try {
      await patch(`/api/accounts/${a.id}`, { enabled })
      onChanged()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <article className={`panel account ${a.enabled ? '' : 'is-disabled'}`}>
      <header className="account-head">
        <div>
          <h3>{a.name}</h3>
          <div className="muted small">
            {providerLabel[a.provider]} · {kindLabel[a.kind]} · priority {a.priority}
            {l?.planType && ` · ${l.planType} plan`}
          </div>
        </div>
        <span className="spacer" />
        <AccountStatus limits={l} enabled={a.enabled} now={now} />
      </header>

      <dl className="facts">
        <div>
          <dt>Credential</dt>
          <dd className="mono">{a.credentialHint}</dd>
        </div>
        {expires !== null && (
          <div>
            <dt>Access token</dt>
            <dd title={fmtDateTime(expires)}>
              {expires > now ? `expires ${fmtRelative(expires, now)}` : 'expired'}
              {a.hasRefreshToken ? ' · auto-refresh' : ''}
            </dd>
          </div>
        )}
        {l?.refreshedAt && (
          <div>
            <dt>Limits checked</dt>
            <dd>{fmtRelative(l.refreshedAt, now)}</dd>
          </div>
        )}
        {l?.unifiedStatus && (
          <div>
            <dt>Provider status</dt>
            <dd>
              {l.unifiedStatus}
              {l.representativeClaim ? ` (${l.representativeClaim})` : ''}
            </dd>
          </div>
        )}
      </dl>

      {l?.exhausted && (
        <div className="callout danger">
          Exhausted{l.exhaustedReason ? ` (${l.exhaustedReason.replace('_', ' ')})` : ''}
          {l.exhaustedUntil ? ` until ${fmtDateTime(l.exhaustedUntil)} (${fmtRelative(l.exhaustedUntil, now)})` : ''}.
        </div>
      )}
      {l?.refreshError && <div className="callout warn">Last limit refresh failed: {l.refreshError}</div>}

      <div className="limits">
        {l && l.windows.length > 0 ? (
          l.windows.map((w) => <LimitBar key={w.key} w={w} now={now} />)
        ) : (
          <div className="muted small">
            No limit data yet.{' '}
            {a.kind.endsWith('oauth') ? 'Refresh to query the provider usage endpoint.' : 'API-key limits appear after the first proxied request.'}
          </div>
        )}
      </div>

      <div className="resets-bar">
        <span>
          <strong>{l?.resets.available ?? 0}</strong> reset{(l?.resets.available ?? 0) === 1 ? '' : 's'} available
          {l?.resets.nextExpiresAt && <span className="muted"> · next expires {fmtRelative(l.resets.nextExpiresAt, now)}</span>}
        </span>
        <span className="spacer" />
        <button className="secondary small" onClick={() => setShowResets((s) => !s)} aria-expanded={showResets}>
          {showResets ? 'Hide resets' : 'Manage resets'}
        </button>
      </div>
      {showResets && (
        <Resets
          account={a}
          now={now}
          onUsed={(nl) => {
            if (nl) setLimits(nl)
            onChanged()
          }}
        />
      )}

      {error && <ErrorBox error={error} />}

      <footer className="account-actions">
        <Toggle checked={a.enabled} onChange={toggleEnabled} label={a.enabled ? 'Enabled' : 'Disabled'} />
        <span className="spacer" />
        <button className="secondary small" onClick={refresh} disabled={refreshing}>
          {refreshing ? 'Refreshing…' : 'Refresh limits'}
        </button>
        <button className="secondary small" onClick={() => setEditing(true)}>
          Edit
        </button>
        <button className="secondary small danger-text" onClick={() => setDeleting(true)}>
          Delete
        </button>
      </footer>

      {editing && (
        <AccountForm
          account={a}
          onClose={() => setEditing(false)}
          onSaved={() => {
            setEditing(false)
            onChanged()
          }}
        />
      )}
      {deleting && (
        <ConfirmDialog
          title="Delete account"
          danger
          confirmLabel="Delete"
          message={
            <p>
              Delete <strong>{a.name}</strong>? Its stored credentials are removed and sessions bound to it move to another account. Usage
              history is kept.
            </p>
          }
          onConfirm={async () => {
            await del(`/api/accounts/${a.id}`)
            onChanged()
          }}
          onClose={() => setDeleting(false)}
        />
      )}
    </article>
  )
}

function Resets({ account, now, onUsed }: { account: Account; now: number; onUsed: (l: AccountLimits | null) => void }) {
  const resets = useApi<ResetCredit[]>(`/api/accounts/${account.id}/resets?includeUsed=true`, undefined, ['limits'])
  const [using, setUsing] = useState<ResetCredit | null>(null)
  const [removing, setRemoving] = useState<ResetCredit | null>(null)
  const [adding, setAdding] = useState(false)

  return (
    <div className="resets">
      <Async state={resets} empty={(r) => (r.length === 0 ? <div className="muted small">No reset credits known for this account.</div> : null)}>
        {(list) => (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Source</th>
                  <th>Expires</th>
                  <th>Note</th>
                  <th>Status</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {list.map((r) => (
                  <tr key={r.id} className={r.available ? '' : 'dim'}>
                    <td>{r.source === 'provider' ? <Badge tone="accent">provider</Badge> : <Badge>manual</Badge>}</td>
                    <td title={fmtDateTime(r.expiresAt)}>{r.expiresAt ? `${fmtDateTime(r.expiresAt)} (${fmtRelative(r.expiresAt, now)})` : 'never'}</td>
                    <td>{r.note ?? ''}</td>
                    <td>
                      {r.usedAt ? `used ${fmtRelative(r.usedAt, now)}${r.usedBy ? ` (${r.usedBy})` : ''}` : r.available ? 'available' : 'expired'}
                    </td>
                    <td className="row-actions">
                      {r.available && (
                        <button className="small" onClick={() => setUsing(r)}>
                          Use reset
                        </button>
                      )}
                      {r.source === 'manual' && !r.usedAt && (
                        <button className="link small" onClick={() => setRemoving(r)}>
                          Remove
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
      {adding ? (
        <AddResetForm
          accountId={account.id}
          onDone={() => {
            setAdding(false)
            resets.reload()
            onUsed(null)
          }}
          onCancel={() => setAdding(false)}
        />
      ) : (
        <button className="link small" onClick={() => setAdding(true)}>
          + Add reset manually
        </button>
      )}
      {using && (
        <ConfirmDialog
          title="Use reset"
          confirmLabel="Use reset now"
          message={
            <>
              <p>
                Reset the limits of <strong>{account.name}</strong> now? This consumes the reset credit
                {using.expiresAt ? ` that expires ${fmtRelative(using.expiresAt, now)}` : ''}.
              </p>
              {using.source === 'manual' && (
                <p className="muted small">This is a manually recorded credit: the gateway only clears its local exhaustion state; redeem it with the provider yourself.</p>
              )}
            </>
          }
          onConfirm={async () => {
            const r = await post<{ ok: boolean; limits: AccountLimits }>(`/api/accounts/${account.id}/resets/${using.id}/use`)
            resets.reload()
            onUsed(r.limits)
          }}
          onClose={() => setUsing(null)}
        />
      )}
      {removing && (
        <ConfirmDialog
          title="Remove reset"
          danger
          confirmLabel="Remove"
          message={<p>Remove this manually added reset credit?</p>}
          onConfirm={async () => {
            await del(`/api/accounts/${account.id}/resets/${removing.id}`)
            resets.reload()
            onUsed(null)
          }}
          onClose={() => setRemoving(null)}
        />
      )}
    </div>
  )
}

function AddResetForm({ accountId, onDone, onCancel }: { accountId: number; onDone: () => void; onCancel: () => void }) {
  const [expiresAt, setExpiresAt] = useState('')
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await post(`/api/accounts/${accountId}/resets`, {
        expiresAt: expiresAt ? new Date(expiresAt).toISOString() : null,
        note: note || null,
      })
      onDone()
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }
  return (
    <form className="inline-form" onSubmit={submit}>
      <label>
        Expires
        <input type="datetime-local" value={expiresAt} onChange={(e) => setExpiresAt(e.target.value)} />
      </label>
      <label className="grow">
        Note
        <input value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. promo reset from support" />
      </label>
      <button type="submit" className="small" disabled={busy}>
        Add
      </button>
      <button type="button" className="link small" onClick={onCancel}>
        Cancel
      </button>
      {error && <p className="error">{error}</p>}
    </form>
  )
}

const KINDS: { kind: AccountKind; title: string; help: React.ReactNode }[] = [
  {
    kind: 'anthropic_oauth',
    title: 'Claude subscription (Pro / Max)',
    help: (
      <>
        Paste one of: the output of <code>claude setup-token</code> (a long-lived <code>sk-ant-oat…</code> token); or the Claude Code credentials
        JSON <code>{'{"claudeAiOauth":{…}}'}</code> from <code>~/.claude/.credentials.json</code> (Linux) or the macOS Keychain item “Claude
        Code-credentials” (<code>security find-generic-password -s "Claude Code-credentials" -w</code>). Tokens with a refresh token are renewed
        automatically.
      </>
    ),
  },
  {
    kind: 'anthropic_api_key',
    title: 'Anthropic API key',
    help: (
      <>
        An API key from the Anthropic Console (<code>sk-ant-api…</code>).
      </>
    ),
  },
  {
    kind: 'chatgpt_oauth',
    title: 'ChatGPT subscription (Codex)',
    help: (
      <>
        Run <code>codex login</code> once, then paste the contents of <code>~/.codex/auth.json</code>. The gateway uses its tokens and account id and
        refreshes them automatically.
      </>
    ),
  },
  {
    kind: 'openai_api_key',
    title: 'OpenAI API key',
    help: (
      <>
        An API key from the OpenAI platform (<code>sk-…</code>).
      </>
    ),
  },
]

function AccountForm({ account, onClose, onSaved }: { account?: Account; onClose: () => void; onSaved: () => void }) {
  const editing = !!account
  const [kind, setKind] = useState<AccountKind>(account?.kind ?? 'anthropic_oauth')
  const [name, setName] = useState(account?.name ?? '')
  const [priority, setPriority] = useState(String(account?.priority ?? 100))
  const [secret, setSecret] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const isKey = kind.endsWith('api_key')
  const info = KINDS.find((k) => k.kind === kind)!
  const allowedKinds = editing
    ? KINDS.filter((k) => (account!.provider === 'anthropic' ? k.kind.startsWith('anthropic') : !k.kind.startsWith('anthropic')))
    : KINDS

  async function submit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    if (!editing && !secret.trim()) {
      setError(isKey ? 'Enter the API key.' : 'Paste the credentials.')
      return
    }
    const body: Record<string, unknown> = { name: name.trim() || null, priority: Number(priority) || 100 }
    if (secret.trim()) {
      body.kind = kind
      if (isKey) body.apiKey = secret.trim()
      else body.credentialsJson = secret.trim()
    }
    setBusy(true)
    try {
      if (editing) await patch(`/api/accounts/${account!.id}`, body)
      else await post('/api/accounts', body)
      onSaved()
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

  return (
    <Modal
      title={editing ? `Edit ${account!.name}` : 'Link account'}
      onClose={onClose}
      footer={
        <>
          <button className="secondary" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button type="submit" form="account-form" disabled={busy}>
            {busy ? 'Saving…' : editing ? 'Save' : 'Link account'}
          </button>
        </>
      }
    >
      <form id="account-form" className="form" onSubmit={submit}>
        <fieldset className="kinds">
          <legend>Type</legend>
          {allowedKinds.map((k) => (
            <label key={k.kind} className={`kind ${kind === k.kind ? 'on' : ''}`}>
              <input type="radio" name="kind" value={k.kind} checked={kind === k.kind} onChange={() => setKind(k.kind)} />
              {k.title}
            </label>
          ))}
        </fieldset>
        <p className="help">{info.help}</p>
        <label>
          {isKey ? 'API key' : 'Credentials'}
          {editing && <span className="muted small"> (leave empty to keep the current {isKey ? 'key' : 'credentials'})</span>}
          {isKey ? (
            <input type="password" autoComplete="off" value={secret} onChange={(e) => setSecret(e.target.value)} placeholder={kind === 'anthropic_api_key' ? 'sk-ant-api03-…' : 'sk-…'} />
          ) : (
            <textarea
              rows={6}
              spellCheck={false}
              className="mono"
              value={secret}
              onChange={(e) => setSecret(e.target.value)}
              placeholder={kind === 'anthropic_oauth' ? '{"claudeAiOauth":{"accessToken":"sk-ant-oat01-…", …}}  or  sk-ant-oat01-…' : '{"OPENAI_API_KEY":null,"tokens":{"id_token":"…","access_token":"…","refresh_token":"…","account_id":"…"}}'}
            />
          )}
        </label>
        <div className="form-row">
          <label className="grow">
            Name
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder={kindLabel[kind]} />
          </label>
          <label>
            Priority
            <input type="number" value={priority} onChange={(e) => setPriority(e.target.value)} style={{ width: 90 }} />
          </label>
        </div>
        <p className="muted small">Credentials are encrypted at rest and never shown again.</p>
        {error && <p className="error">{error}</p>}
      </form>
    </Modal>
  )
}
