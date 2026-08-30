import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { useHostnames } from '@/lib/use-hostnames'

const fetchJson = vi.hoisted(() => vi.fn())
vi.mock('@/lib/api', () => ({ fetchJson }))

/**
 * Stands in for the browser's observer, which jsdom does not implement. Holding the
 * instances lets a test drive `scrollTo` the way a scroll would, which is the only
 * way to assert the thing that matters here: what is on screen is what gets asked for.
 */
class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = []

  readonly observed = new Set<Element>()

  readonly notify: IntersectionObserverCallback

  readonly options?: IntersectionObserverInit

  constructor(notify: IntersectionObserverCallback, options?: IntersectionObserverInit) {
    this.notify = notify
    this.options = options
    FakeIntersectionObserver.instances.push(this)
  }

  observe(element: Element) {
    this.observed.add(element)
  }

  unobserve(element: Element) {
    this.observed.delete(element)
  }

  disconnect() {
    this.observed.clear()
  }

  /** What the browser does when these elements come into view. */
  scrollTo(...elements: Element[]) {
    this.notify(
      elements.map((target) => ({ target, isIntersecting: true }) as IntersectionObserverEntry),
      this as unknown as IntersectionObserver,
    )
  }

  static get current() {
    const observer = FakeIntersectionObserver.instances.at(-1)
    if (!observer) throw new Error('nothing was ever observed')
    return observer
  }
}

/** The requested addresses, in order, of every call made so far. */
function requestedIps(): string[][] {
  return fetchJson.mock.calls.map((call) =>
    decodeURIComponent(String(call[0]).split('ips=')[1]).split(','),
  )
}

/** Attaches the hook's ref for each address and returns the elements it produced. */
function attach(observeSource: (ip: string) => (node: Element | null) => void, ips: string[]) {
  return ips.map((ip) => {
    const element = document.createElement('tbody')
    observeSource(ip)(element)
    return element
  })
}

beforeEach(() => {
  vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver)
  FakeIntersectionObserver.instances = []
  fetchJson.mockReset()
  fetchJson.mockResolvedValue({})
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('useHostnames', () => {
  it('asks only for the rows that come into view', () => {
    // The code this replaced resolved the first 100 sources in server order, which on
    // a 1176-row table sorted by a column meant the hostnames shown bore no relation
    // to the rows on screen.
    const { result } = renderHook(() => useHostnames())
    const [onScreen, , offScreen] = attach(result.current.observeSource, [
      '192.0.2.1',
      '192.0.2.2',
      '198.51.100.9',
    ])

    act(() => {
      FakeIntersectionObserver.current.scrollTo(onScreen)
      vi.advanceTimersByTime(200)
    })

    expect(requestedIps()).toEqual([['192.0.2.1']])
    expect(FakeIntersectionObserver.current.observed.has(offScreen)).toBe(true)
  })

  it('batches everything revealed in one scroll into a single request', () => {
    const { result } = renderHook(() => useHostnames())
    const rows = attach(result.current.observeSource, ['192.0.2.1', '192.0.2.2', '192.0.2.3'])

    act(() => {
      FakeIntersectionObserver.current.scrollTo(...rows)
      vi.advanceTimersByTime(200)
    })

    expect(requestedIps()).toEqual([['192.0.2.1', '192.0.2.2', '192.0.2.3']])
  })

  it('splits a batch at the endpoint ceiling of 100 addresses', () => {
    const ips = Array.from({ length: 150 }, (_, index) => `192.0.2.${index}`)
    const { result } = renderHook(() => useHostnames())
    const rows = attach(result.current.observeSource, ips)

    act(() => {
      FakeIntersectionObserver.current.scrollTo(...rows)
      vi.advanceTimersByTime(200)
    })
    act(() => {
      vi.advanceTimersByTime(200)
    })

    // Two requests, and the second waits a window rather than going out alongside
    // the first — a flick down a long table should not become a burst.
    expect(requestedIps().map((batch) => batch.length)).toEqual([100, 50])
    expect(requestedIps().flat()).toEqual(ips)
  })

  it('asks for an address once, however often its row is scrolled past', () => {
    const { result } = renderHook(() => useHostnames())
    const [row] = attach(result.current.observeSource, ['192.0.2.1'])

    act(() => {
      FakeIntersectionObserver.current.scrollTo(row)
      vi.advanceTimersByTime(200)
    })
    act(() => {
      FakeIntersectionObserver.current.scrollTo(row)
      vi.advanceTimersByTime(200)
    })

    expect(fetchJson).toHaveBeenCalledTimes(1)
    // The row stops being watched once it has been asked about.
    expect(FakeIntersectionObserver.current.observed.size).toBe(0)
  })

  it('exposes what came back, including addresses with no PTR record', async () => {
    fetchJson.mockResolvedValue({ '192.0.2.1': 'mail.example.com', '192.0.2.2': null })
    const { result } = renderHook(() => useHostnames())
    const rows = attach(result.current.observeSource, ['192.0.2.1', '192.0.2.2'])

    act(() => {
      FakeIntersectionObserver.current.scrollTo(...rows)
      vi.advanceTimersByTime(200)
    })

    await act(async () => {})

    // The null matters: it says the address was asked about and has no PTR record,
    // which the table has to tell apart from an address it has not asked about yet.
    expect(result.current.hostnames).toEqual({
      '192.0.2.1': 'mail.example.com',
      '192.0.2.2': null,
    })
  })

  it('keeps the table usable, and stops asking, when a lookup fails', async () => {
    fetchJson.mockRejectedValue(new Error('nope'))
    const { result } = renderHook(() => useHostnames())
    const [row] = attach(result.current.observeSource, ['192.0.2.1'])

    act(() => {
      FakeIntersectionObserver.current.scrollTo(row)
      vi.advanceTimersByTime(200)
    })
    await act(async () => {})

    expect(result.current.hostnames).toEqual({})

    // Enrichment is best-effort: a failure must not turn every later scroll past
    // this row into another doomed request.
    act(() => {
      FakeIntersectionObserver.current.scrollTo(row)
      vi.advanceTimersByTime(200)
    })
    expect(fetchJson).toHaveBeenCalledTimes(1)
  })

  it('resolves on attach where the browser cannot report visibility', () => {
    // jsdom, and browsers old enough to lack the API. Without this the table would
    // show no hostname at all rather than falling back to asking eagerly.
    vi.stubGlobal('IntersectionObserver', undefined)
    const { result } = renderHook(() => useHostnames())
    attach(result.current.observeSource, ['192.0.2.1', '192.0.2.2'])

    act(() => {
      vi.advanceTimersByTime(200)
    })

    expect(requestedIps()).toEqual([['192.0.2.1', '192.0.2.2']])
  })
})
