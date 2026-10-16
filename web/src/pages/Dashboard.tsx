import { useAuth } from '../auth'

export default function Dashboard() {
  const { user, logout } = useAuth()
  return (
    <div className="shell">
      <header className="topbar">
        <strong>AI Coding Gateway</strong>
        <span className="spacer" />
        <span className="muted">{user}</span>
        <button className="link" onClick={() => void logout()}>
          Sign out
        </button>
      </header>
      <main className="content">
        <h2>Overview</h2>
        <p className="muted">Usage statistics will appear here.</p>
      </main>
    </div>
  )
}
