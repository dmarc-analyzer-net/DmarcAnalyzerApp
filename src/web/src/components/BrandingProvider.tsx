import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'

import { fetchJson } from '@/lib/api'
import {
  applyBranding,
  BrandingContext,
  DEFAULT_BRANDING,
  parseBranding,
  readCachedBranding,
  writeCachedBranding,
} from '@/lib/branding'
import type { Branding } from '@/lib/branding'

/**
 * Loads the instance brand from GET /api/v1/branding. The last one seen is
 * cached, so a returning visitor paints the agency's brand at once; on a first
 * visit the logo slot stays empty until the fetch lands, rather than flashing the
 * product's brand at a client who should never see it. A failed fetch falls back
 * to the product's brand — an empty header forever would be worse.
 */
export function BrandingProvider({ children }: { children: ReactNode }) {
  const [branding, setBranding] = useState<Branding | null>(readCachedBranding)

  useEffect(() => {
    let cancelled = false

    fetchJson<unknown>('/api/v1/branding')
      .then((payload) => {
        const next = parseBranding(payload) ?? DEFAULT_BRANDING
        // Cached even when it is the default, so an instance that drops its
        // branding stops painting the old brand on the next visit.
        writeCachedBranding(next)
        return next
      })
      .catch(() => DEFAULT_BRANDING)
      .then((next) => {
        if (!cancelled) setBranding(next)
      })

    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    if (branding) applyBranding(branding)
  }, [branding])

  return <BrandingContext.Provider value={branding}>{children}</BrandingContext.Provider>
}
