import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { authApi } from './api'

type AuthState = {
  user: string | null
  loading: boolean
  login: (password: string) => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    authApi
      .me()
      .then((r) => setUser(r.user))
      .catch(() => setUser(null))
      .finally(() => setLoading(false))
  }, [])

  const login = useCallback(async (password: string) => {
    const r = await authApi.login(password)
    setUser(r.user)
  }, [])

  const logout = useCallback(async () => {
    await authApi.logout()
    setUser(null)
  }, [])

  return <AuthContext.Provider value={{ user, loading, login, logout }}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}
