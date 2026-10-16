import { useEffect, useState, type ReactNode } from 'react'
import type { LimitWindow, AccountLimits } from '../types'
import { fmtDateTime, fmtPct, fmtRelative, statusTone, toMs } from '../format'

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return <div className="state muted">{label}</div>
}

export function ErrorBox({ error, onRetry }: { error: string; onRetry?: () => void }) {
  return (
    <div className="state error-box" role="alert">
      <span>{error}</span>
      {onRetry && (
        <button className="secondary small" onClick={onRetry}>
          Retry
        </button>
      )}
    </div>
  )
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="state muted">{children}</div>
}

/** Shows loading / error / content for one fetch, keeping stale data visible while reloading. */
export function Async<T>({
  state,
  children,
  empty,
}: {
  state: { data: T | undefined; error: string | null; loading: boolean; reload: () => void }
  children: (data: T) => ReactNode
  empty?: (data: T) => ReactNode | null
}) {
  if (state.data === undefined) {
    if (state.error) return <ErrorBox error={state.error} onRetry={state.reload} />
    return state.loading ? <Loading /> : <Empty>No data.</Empty>
  }
  const e = empty?.(state.data)
  return (
    <>
      {state.error && <ErrorBox error={state.error} onRetry={state.reload} />}
      {e ?? children(state.data)}
    </>
  )
}

export function Panel({
  title,
  actions,
  children,
  className = '',
}: {
  title?: ReactNode
  actions?: ReactNode
  children: ReactNode
  className?: string
}) {
  return (
    <section className={`panel ${className}`}>
      {(title || actions) && (
        <header className="panel-head">
          {title && <h3>{title}</h3>}
          <span className="spacer" />
          {actions}
        </header>
      )}
      {children}
    </section>
  )
}

export function Kpi({ label, value, sub, tone }: { label: string; value: ReactNode; sub?: ReactNode; tone?: 'danger' }) {
  return (
    <div className={`kpi ${tone ?? ''}`}>
      <div className="kpi-label">{label}</div>
      <div className="kpi-value">{value}</div>
      {sub !== undefined && <div className="kpi-sub muted">{sub}</div>}
    </div>
  )
}

export function Badge({ tone = 'neutral', children }: { tone?: 'neutral' | 'ok' | 'warn' | 'danger' | 'accent'; children: ReactNode }) {
  return <span className={`badge ${tone}`}>{children}</span>
}

export function LimitBar({ w, now, compact }: { w: LimitWindow; now: number; compact?: boolean }) {
  const pct = w.stale ? 0 : Math.max(0, Math.min(100, w.utilization ?? (w.limit && w.used !== null ? (w.used / w.limit) * 100 : 0)))
  const tone = statusTone(w.stale ? 'ok' : w.status)
  const detail =
    w.limit !== null && w.limit !== undefined
      ? `${(w.used ?? (w.remaining !== null ? w.limit - w.remaining : 0)).toLocaleString()} / ${w.limit.toLocaleString()}`
      : null
  return (
    <div className={`limit ${compact ? 'compact' : ''}`}>
      <div className="limit-row">
        <span className="limit-label">{w.label}</span>
        <span className="spacer" />
        <span className={`limit-pct ${tone}`}>{fmtPct(w.stale ? 0 : (w.utilization ?? pct))}</span>
      </div>
      <div className="bar" role="progressbar" aria-valuenow={Math.round(pct)} aria-valuemin={0} aria-valuemax={100} aria-label={w.label}>
        <div className={`bar-fill ${tone}`} style={{ width: `${pct}%` }} />
      </div>
      {!compact && (
        <div className="limit-meta muted">
          {detail && <span>{detail}</span>}
          {w.resetsAt ? (
            <span title={fmtDateTime(w.resetsAt)}>
              {w.stale ? 'reset ' : 'resets '}
              {fmtRelative(w.resetsAt, now)} · {fmtDateTime(w.resetsAt)}
            </span>
          ) : (
            <span>reset time unknown</span>
          )}
          <span className="spacer" />
          <span title={`Updated ${fmtDateTime(w.updatedAt)}`}>{w.source === 'usage_endpoint' ? 'usage API' : w.source}</span>
        </div>
      )}
      {compact && w.resetsAt && <div className="limit-meta muted">resets {fmtRelative(w.resetsAt, now)}</div>}
    </div>
  )
}

export function AccountStatus({ limits, enabled, now }: { limits: AccountLimits | null; enabled: boolean; now: number }) {
  if (!enabled) return <Badge>disabled</Badge>
  if (!limits) return <Badge>no data</Badge>
  if (limits.exhausted) {
    const until = toMs(limits.exhaustedUntil)
    return (
      <Badge tone="danger">
        exhausted{until ? ` · ${fmtRelative(until, now)}` : ''}
      </Badge>
    )
  }
  if (limits.windows.some((w) => !w.stale && w.status === 'warning')) return <Badge tone="warn">near limit</Badge>
  return <Badge tone="ok">ok</Badge>
}

export function Modal({ title, onClose, children, footer }: { title: string; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-label={title}>
        <header className="modal-head">
          <h3>{title}</h3>
          <span className="spacer" />
          <button className="icon" onClick={onClose} aria-label="Close">
            ×
          </button>
        </header>
        <div className="modal-body">{children}</div>
        {footer && <footer className="modal-foot">{footer}</footer>}
      </div>
    </div>
  )
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel = 'Confirm',
  danger,
  onConfirm,
  onClose,
}: {
  title: string
  message: ReactNode
  confirmLabel?: string
  danger?: boolean
  onConfirm: () => Promise<void> | void
  onClose: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  return (
    <Modal
      title={title}
      onClose={onClose}
      footer={
        <>
          <button className="secondary" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button
            className={danger ? 'danger' : ''}
            disabled={busy}
            onClick={async () => {
              setBusy(true)
              setError(null)
              try {
                await onConfirm()
                onClose()
              } catch (e) {
                setError((e as Error).message)
                setBusy(false)
              }
            }}
          >
            {busy ? 'Working…' : confirmLabel}
          </button>
        </>
      }
    >
      <div>{message}</div>
      {error && <p className="error">{error}</p>}
    </Modal>
  )
}

export function CopyButton({ text, label = 'Copy' }: { text: string; label?: string }) {
  const [done, setDone] = useState(false)
  return (
    <button
      className="secondary small"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text)
        } catch {
          const ta = document.createElement('textarea')
          ta.value = text
          document.body.appendChild(ta)
          ta.select()
          document.execCommand('copy')
          ta.remove()
        }
        setDone(true)
        window.setTimeout(() => setDone(false), 1500)
      }}
    >
      {done ? 'Copied' : label}
    </button>
  )
}

export function CodeBlock({ code }: { code: string }) {
  return (
    <div className="code">
      <pre>{code}</pre>
      <CopyButton text={code} />
    </div>
  )
}

export function Toggle({ checked, onChange, label, disabled }: { checked: boolean; onChange: (v: boolean) => void; label: string; disabled?: boolean }) {
  return (
    <label className={`toggle ${disabled ? 'disabled' : ''}`}>
      <input type="checkbox" role="switch" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      <span className="toggle-track" aria-hidden="true">
        <span className="toggle-thumb" />
      </span>
      <span>{label}</span>
    </label>
  )
}
