export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    credentials: 'same-origin',
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
  })
  const text = await res.text()
  const body = text ? JSON.parse(text) : undefined
  if (!res.ok) throw new ApiError(res.status, body?.error ?? res.statusText)
  return body as T
}

export const authApi = {
  me: () => api<{ user: string }>('/api/auth/me'),
  login: (password: string) =>
    api<{ user: string }>('/api/auth/login', { method: 'POST', body: JSON.stringify({ password }) }),
  logout: () => api<void>('/api/auth/logout', { method: 'POST' }),
}
