import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { AuthProvider } from './auth'
import { useAuth } from './authContext'
import Layout from './components/Layout'
import { RangeProvider } from './range'
import Accounts from './pages/Accounts'
import Analytics from './pages/Analytics'
import Login from './pages/Login'
import Overview from './pages/Overview'
import Requests from './pages/Requests'
import SessionDetail from './pages/SessionDetail'
import Sessions from './pages/Sessions'
import Settings from './pages/Settings'

function RequireAuth({ children }: { children: React.ReactElement }) {
  const { user, loading } = useAuth()
  if (loading) return <div className="center muted">Loading…</div>
  return user ? children : <Navigate to="/login" replace />
}

export default function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route
            element={
              <RequireAuth>
                <RangeProvider>
                  <Layout />
                </RangeProvider>
              </RequireAuth>
            }
          >
            <Route index element={<Overview />} />
            <Route path="analytics" element={<Analytics />} />
            <Route path="accounts" element={<Accounts />} />
            <Route path="sessions" element={<Sessions />} />
            <Route path="sessions/:provider/:sessionId" element={<SessionDetail />} />
            <Route path="requests" element={<Requests />} />
            <Route path="settings" element={<Settings />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  )
}
