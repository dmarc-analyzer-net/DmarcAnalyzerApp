import { useEffect, useState } from 'react'

import { fetchJson } from '@/lib/api'

let statusPromise: Promise<string> | null = null

function loadSystemStatus(): Promise<string> {
  statusPromise ??= fetchJson<{ service: string; mode: string; timestampUtc: string }>(
    '/api/v1/system/status',
  )
    .then((payload) => `${payload.service} (${payload.mode}) at ${payload.timestampUtc}`)
    .catch((statusError: unknown) =>
      statusError instanceof Error ? `API unavailable: ${statusError.message}` : 'API unavailable',
    )
  return statusPromise
}

/** System status line, fetched once per app load and shared across pages. */
export function useSystemStatus(): string {
  const [status, setStatus] = useState('Loading API status...')

  useEffect(() => {
    let cancelled = false
    void loadSystemStatus().then((value) => {
      if (!cancelled) setStatus(value)
    })
    return () => {
      cancelled = true
    }
  }, [])

  return status
}
