import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { AuthProvider, useAuth } from './auth'
import Dashboard from './pages/Dashboard'
import Login from './pages/Login'

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
            path="/*"
            element={
              <RequireAuth>
                <Dashboard />
              </RequireAuth>
            }
          />
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  )
}
