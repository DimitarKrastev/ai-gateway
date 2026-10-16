import { NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../authContext'
import { useRange, type RangePreset } from '../rangeContext'

const NAV = [
  { to: '/', label: 'Overview', end: true },
  { to: '/analytics', label: 'Analytics' },
  { to: '/accounts', label: 'Accounts' },
  { to: '/sessions', label: 'Sessions' },
  { to: '/requests', label: 'Requests' },
  { to: '/settings', label: 'Settings' },
]

export default function Layout() {
  const { user, logout } = useAuth()
  return (
    <div className="shell">
      <header className="topbar">
        <strong className="brand">AI Coding Gateway</strong>
        <nav className="nav">
          {NAV.map((n) => (
            <NavLink key={n.to} to={n.to} end={n.end} className={({ isActive }) => (isActive ? 'active' : '')}>
              {n.label}
            </NavLink>
          ))}
        </nav>
        <span className="spacer" />
        <span className="muted user">{user}</span>
        <button className="link" onClick={() => void logout()}>
          Sign out
        </button>
      </header>
      <main className="content">
        <Outlet />
      </main>
    </div>
  )
}

const PRESETS: RangePreset[] = ['1h', '24h', '7d', '30d', 'custom']

export function RangePicker() {
  const { range, setRange, refresh } = useRange()
  return (
    <div className="range">
      <div className="seg" role="group" aria-label="Time range">
        {PRESETS.map((p) => (
          <button key={p} className={range.preset === p ? 'on' : ''} onClick={() => setRange({ ...range, preset: p })}>
            {p === 'custom' ? 'Custom' : p}
          </button>
        ))}
      </div>
      {range.preset === 'custom' && (
        <>
          <input
            type="datetime-local"
            aria-label="From"
            value={range.customFrom}
            onChange={(e) => setRange({ ...range, customFrom: e.target.value })}
          />
          <span className="muted">→</span>
          <input
            type="datetime-local"
            aria-label="To"
            value={range.customTo}
            onChange={(e) => setRange({ ...range, customTo: e.target.value })}
          />
        </>
      )}
      <button className="secondary small" onClick={refresh} title="Refresh now">
        ↻
      </button>
    </div>
  )
}

export function PageHead({ title, children }: { title: string; children?: React.ReactNode }) {
  return (
    <div className="page-head">
      <h2>{title}</h2>
      <span className="spacer" />
      {children}
    </div>
  )
}
