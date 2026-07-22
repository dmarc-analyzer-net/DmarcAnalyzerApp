export class ApiError extends Error {
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

type UnauthorizedHandler = () => void

let onUnauthorized: UnauthorizedHandler | null = null

/**
 * Registers a callback invoked whenever a non-auth API call returns 401,
 * so the app can drop back to the login screen on session expiry.
 */
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  onUnauthorized = handler
}

const isAuthPath = (url: string) => url.startsWith('/api/v1/auth/')

export async function fetchJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, credentials: 'include' })

  if (!response.ok) {
    let message = `Request failed (${response.status})`
    try {
      const payload = (await response.json()) as { error?: string }
      if (payload.error) message = payload.error
    } catch {
      // ignore json parse errors
    }

    if (response.status === 401 && !isAuthPath(url)) {
      onUnauthorized?.()
    }

    throw new ApiError(message, response.status)
  }

  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}
