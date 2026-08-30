import { useCallback, useEffect, useRef, useState } from 'react'

import { fetchJson } from '@/lib/api'

/** The batch endpoint's own ceiling on how many addresses one request may carry. */
const MAX_IPS_PER_REQUEST = 100

/**
 * How far outside the viewport a row already counts as visible. Roughly a screen
 * of lead time, so on an ordinary scroll the hostname is under the IP before the
 * row itself arrives rather than appearing a beat after it.
 */
const PREFETCH_MARGIN = '400px'

/**
 * How long intersections accumulate before a request goes out. A scroll reveals
 * rows a handful at a time and each would otherwise be its own round trip; this
 * turns a flick down the table into a few batched lookups.
 */
const BATCH_WINDOW_MS = 120

export interface HostnameLookup {
  /**
   * Resolved reverse-DNS names, keyed by the address exactly as it was asked for.
   * A null value is a lookup that came back with nothing — the address was asked
   * about and has no PTR record, which is different from not yet being asked.
   */
  hostnames: Record<string, string | null>
  /**
   * Ref callback for the element that renders `ip`. Attaching it is the whole
   * subscription: the address is looked up when the element first comes near the
   * viewport, once, and never again.
   */
  observeSource: (ip: string) => (node: Element | null) => void
}

/**
 * Reverse-DNS enrichment for a long list of addresses, resolved as the user
 * scrolls rather than up front.
 *
 * The sources table renders every row it has — a real domain reached 1176 — and
 * PTR lookups are far too expensive to do for all of them on load. This used to
 * be handled by resolving `sources.slice(0, 100)`, which had two problems: the
 * other 1076 rows were never enriched at all, and the 100 came off the server's
 * order while the table renders the user's chosen sort, so which rows got a
 * hostname bore no relation to which rows were on screen.
 *
 * Tying the lookup to visibility fixes both at once and costs less than either:
 * whatever you are actually looking at is resolved, in any sort order, and a
 * table nobody scrolls resolves one screenful.
 */
export function useHostnames(): HostnameLookup {
  const [hostnames, setHostnames] = useState<Record<string, string | null>>({})

  /**
   * Every address already sent — resolved, in flight, or failed. A ref rather
   * than something derived from `hostnames`, because a row can be observed again
   * before the request it triggered has landed and state would not have caught up.
   */
  const asked = useRef(new Set<string>())
  const queued = useRef(new Set<string>())
  const flushTimer = useRef<number | null>(null)
  const mounted = useRef(true)
  const observer = useRef<IntersectionObserver | null>(null)
  const addressOf = useRef(new WeakMap<Element, string>())
  const refCallbacks = useRef(new Map<string, (node: Element | null) => void>())

  const flush = useCallback(() => {
    flushTimer.current = null
    if (!mounted.current) return

    const batch: string[] = []
    for (const ip of queued.current) {
      if (batch.length === MAX_IPS_PER_REQUEST) break
      batch.push(ip)
    }
    for (const ip of batch) queued.current.delete(ip)
    if (batch.length === 0) return

    // A queue longer than one batch waits for the next window instead of going
    // out as a second request now. Scrolling fast past hundreds of rows should
    // still cost a request every BATCH_WINDOW_MS, not a burst of them.
    if (queued.current.size > 0) {
      flushTimer.current = window.setTimeout(flush, BATCH_WINDOW_MS)
    }

    void fetchJson<Record<string, string | null>>(
      `/api/v1/analytics/hostnames?ips=${encodeURIComponent(batch.join(','))}`,
    )
      .then((resolved) => {
        if (mounted.current) setHostnames((prev) => ({ ...prev, ...resolved }))
      })
      .catch(() => {
        // Enrichment is best-effort; the row keeps its IP. The batch stays marked
        // as asked, so a failing lookup is not retried every time the row is
        // scrolled past.
      })
  }, [])

  const request = useCallback(
    (ip: string) => {
      if (ip.length === 0 || asked.current.has(ip)) return
      asked.current.add(ip)
      queued.current.add(ip)
      if (flushTimer.current === null) {
        flushTimer.current = window.setTimeout(flush, BATCH_WINDOW_MS)
      }
    },
    [flush],
  )

  const getObserver = useCallback(() => {
    observer.current ??= new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue
          const ip = addressOf.current.get(entry.target)
          if (ip !== undefined) request(ip)
          // An address resolves once. Keeping the row observed after that would
          // fire on every scroll past it for no further effect.
          observer.current?.unobserve(entry.target)
        }
      },
      { rootMargin: PREFETCH_MARGIN },
    )
    return observer.current
  }, [request])

  const observeSource = useCallback(
    (ip: string) => {
      // One stable callback per address. Building a fresh function per render
      // would make React detach and re-attach the ref on every render of a
      // thousand-row table, which re-runs this for every row each time.
      let callback = refCallbacks.current.get(ip)
      if (callback === undefined) {
        callback = (node: Element | null) => {
          if (node === null || asked.current.has(ip)) return

          // No IntersectionObserver means no way to tell what is on screen, so
          // resolve on attach. That is the pre-scroll behaviour, batched — it
          // reaches jsdom under test and browsers old enough not to have the API.
          if (typeof IntersectionObserver === 'undefined') {
            request(ip)
            return
          }

          addressOf.current.set(node, ip)
          const active = getObserver()
          active.observe(node)
          return () => active.unobserve(node)
        }
        refCallbacks.current.set(ip, callback)
      }
      return callback
    },
    [getObserver, request],
  )

  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
      if (flushTimer.current !== null) window.clearTimeout(flushTimer.current)
      flushTimer.current = null
      observer.current?.disconnect()
      observer.current = null
    }
  }, [])

  return { hostnames, observeSource }
}
