import { createContext, useContext } from 'react'

export type AuthUser = {
  id: string
  email: string
  displayName: string
  role: string
  isActive: boolean
  lastLoginAtUtc: string | null
  createdAtUtc: string
  updatedAtUtc: string
}

export type AuthStatus = 'loading' | 'authenticated' | 'unauthenticated'

export type AuthContextValue = {
  status: AuthStatus
  user: AuthUser | null
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used within an AuthProvider')
  }
  return context
}
