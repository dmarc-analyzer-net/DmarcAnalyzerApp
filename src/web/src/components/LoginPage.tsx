import { Loader2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'

import { BrandLogo } from '@/components/BrandLogo'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'

type OidcProvider = {
  enabled: boolean
  displayName: string
  loginUrl: string
}

const OIDC_ERROR_MESSAGES: Record<string, string> = {
  oidc_failed: 'Single sign-on failed. Try again or use your password.',
  email_not_verified:
    'Your email address is not verified with the identity provider, so it cannot be linked to an existing account.',
  no_account: 'No account exists for your identity. Ask an administrator to create one.',
  account_disabled: 'Your account is deactivated. Ask an administrator to re-enable it.',
}

export function LoginPage() {
  const { login } = useAuth()

  // null = setup check in flight; falls back to the login form if it fails.
  const [requiresBootstrap, setRequiresBootstrap] = useState<boolean | null>(null)
  const [oidcProvider, setOidcProvider] = useState<OidcProvider | null>(null)

  const [displayName, setDisplayName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(() => {
    const code = new URLSearchParams(window.location.search).get('loginError')
    if (!code) return null
    return OIDC_ERROR_MESSAGES[code] ?? 'Single sign-on failed. Try again or use your password.'
  })
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    let cancelled = false

    const checkSetup = async () => {
      try {
        const [setup, providers] = await Promise.all([
          fetchJson<{ requiresBootstrap: boolean }>('/api/v1/auth/setup'),
          fetchJson<{ local: boolean; oidc: OidcProvider | null }>('/api/v1/auth/providers').catch(
            () => ({ local: true, oidc: null }),
          ),
        ])
        if (!cancelled) {
          setRequiresBootstrap(setup.requiresBootstrap)
          setOidcProvider(providers.oidc)
        }
      } catch {
        if (!cancelled) setRequiresBootstrap(false)
      }
    }

    void checkSetup()
    return () => {
      cancelled = true
    }
  }, [])

  const startSso = () => {
    const returnUrl = window.location.pathname + window.location.search
    window.location.assign(
      `${oidcProvider!.loginUrl}?returnUrl=${encodeURIComponent(returnUrl === '/' ? '/dashboard' : returnUrl)}`,
    )
  }

  const handleLogin = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await login(email, password)
    } catch (loginError) {
      if (loginError instanceof ApiError && loginError.status === 401) {
        setError('Invalid email or password')
      } else {
        setError('Unable to sign in. Check your connection and try again.')
      }
      setSubmitting(false)
    }
  }

  const handleBootstrap = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await fetchJson('/api/v1/auth/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password, displayName }),
      })
      await login(email, password)
    } catch (bootstrapError) {
      if (bootstrapError instanceof ApiError && bootstrapError.status === 403) {
        // Someone else completed setup first; drop back to the login form.
        setRequiresBootstrap(false)
        setError('Setup has already been completed. Sign in with your credentials.')
      } else if (bootstrapError instanceof ApiError) {
        setError(bootstrapError.message)
      } else {
        setError('Unable to create the account. Check your connection and try again.')
      }
      setSubmitting(false)
    }
  }

  if (requiresBootstrap === null) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <Loader2 className="h-6 w-6 animate-spin text-secondary" aria-label="Loading" />
      </div>
    )
  }

  return (
    <div className="flex min-h-screen items-center justify-center px-4 py-6">
      <Card className="w-full max-w-md">
        <CardHeader className="flex-col items-stretch">
          <BrandLogo className="mb-5 h-auto w-full max-w-[205px]" />
          <CardTitle>{requiresBootstrap ? 'Welcome' : 'Sign in'}</CardTitle>
          <CardDescription className="mt-1">
            {requiresBootstrap
              ? 'Create the first administrator account to set up the Operations Console.'
              : 'Enter your credentials to access the Operations Console.'}
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form className="grid gap-3" onSubmit={requiresBootstrap ? handleBootstrap : handleLogin}>
            {!!error && (
              <p className="rounded-md border border-red-600/25 bg-red-100 px-3 py-2 text-sm text-red-800">
                {error}
              </p>
            )}
            {requiresBootstrap && (
              <div className="grid gap-1.5">
                <label htmlFor="login-display-name" className="text-sm font-medium">
                  Display name
                </label>
                <Input
                  id="login-display-name"
                  autoComplete="name"
                  placeholder="Your name"
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  autoFocus
                  required
                />
              </div>
            )}
            <div className="grid gap-1.5">
              <label htmlFor="login-email" className="text-sm font-medium">
                Email
              </label>
              <Input
                id="login-email"
                type="email"
                autoComplete="email"
                placeholder="you@agency.com"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                autoFocus={!requiresBootstrap}
                required
              />
            </div>
            <div className="grid gap-1.5">
              <label htmlFor="login-password" className="text-sm font-medium">
                Password
              </label>
              <Input
                id="login-password"
                type="password"
                autoComplete={requiresBootstrap ? 'new-password' : 'current-password'}
                placeholder={requiresBootstrap ? 'Choose a password' : 'Your password'}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
              />
            </div>
            <Button type="submit" className="mt-1 w-full" disabled={submitting}>
              {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
              {requiresBootstrap ? 'Create administrator account' : 'Sign in'}
            </Button>
          </form>
          {!requiresBootstrap && oidcProvider?.enabled && (
            <>
              <div className="my-4 flex items-center gap-3">
                <div className="h-px flex-1 bg-border" />
                <span className="text-xs uppercase tracking-wide text-secondary">or</span>
                <div className="h-px flex-1 bg-border" />
              </div>
              <Button
                type="button"
                variant="outline"
                className="w-full"
                onClick={startSso}
                disabled={submitting}
              >
                Sign in with {oidcProvider.displayName}
              </Button>
            </>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
