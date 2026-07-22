import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'

import { fetchJson, setUnauthorizedHandler } from '@/lib/api'
import { AuthContext } from '@/lib/auth-context'
import type { AuthStatus, AuthUser } from '@/lib/auth-context'

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('loading')
  const [user, setUser] = useState<AuthUser | null>(null)

  useEffect(() => {
    setUnauthorizedHandler(() => {
      setUser(null)
      setStatus('unauthenticated')
    })
    return () => setUnauthorizedHandler(null)
  }, [])

  useEffect(() => {
    let cancelled = false

    const checkSession = async () => {
      try {
        const payload = await fetchJson<{ user: AuthUser }>('/api/v1/auth/me')
        if (cancelled) return
        setUser(payload.user)
        setStatus('authenticated')
      } catch {
        if (cancelled) return
        setUser(null)
        setStatus('unauthenticated')
      }
    }

    void checkSession()
    return () => {
      cancelled = true
    }
  }, [])

  const login = useCallback(async (email: string, password: string) => {
    const payload = await fetchJson<{ user: AuthUser }>('/api/v1/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    })
    setUser(payload.user)
    setStatus('authenticated')
  }, [])

  const logout = useCallback(async () => {
    try {
      await fetchJson('/api/v1/auth/logout', { method: 'POST' })
    } catch {
      // clear the local session even if the logout request fails
    }
    setUser(null)
    setStatus('unauthenticated')
  }, [])

  const value = useMemo(
    () => ({ status, user, login, logout }),
    [status, user, login, logout],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
