import { act, renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { loadErrorMessage, useLoader } from '@/lib/use-loader'

/** A promise the test settles by hand, so the order requests answer in is the test's to choose. */
function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

describe('useLoader', () => {
  it('is loading from the first render until the answer lands', async () => {
    const request = deferred<string>()
    const load = () => request.promise
    const { result } = renderHook(() => useLoader(load))

    // No render without a spinner: the page must not flash "no data" before its first request.
    expect(result.current).toMatchObject({ loading: true, data: undefined, error: null })

    await act(async () => request.resolve('first'))
    expect(result.current).toMatchObject({ loading: false, data: 'first', error: null })
  })

  it('reports a failure and keeps what it had', async () => {
    let fail = false
    const load = () => (fail ? Promise.reject(new Error('down')) : Promise.resolve('kept'))
    const { result } = renderHook(() => useLoader(load))
    await waitFor(() => expect(result.current.data).toBe('kept'))

    fail = true
    // Started inside act, awaited outside it: act holds back the render the reload waits for.
    let reloaded: Promise<string | undefined> | undefined
    act(() => {
      reloaded = result.current.reload()
    })
    await expect(reloaded).resolves.toBeUndefined()
    await waitFor(() => expect(result.current).toMatchObject({ loading: false, data: 'kept' }))
    expect(loadErrorMessage(result.current.error, 'fallback')).toBe('down')
  })

  it('clears the error as soon as a reload starts', async () => {
    let attempt = 0
    const retry = deferred<string>()
    const load = () => (++attempt === 1 ? Promise.reject(new Error('down')) : retry.promise)
    const { result } = renderHook(() => useLoader(load))
    await waitFor(() => expect(result.current.error).not.toBeNull())

    let reloaded: Promise<string | undefined> | undefined
    act(() => {
      reloaded = result.current.reload()
    })
    expect(result.current).toMatchObject({ loading: true, error: null })

    await act(async () => retry.resolve('back'))
    await expect(reloaded).resolves.toBe('back')
    await waitFor(() =>
      expect(result.current).toMatchObject({ loading: false, data: 'back', error: null }),
    )
  })

  it('reloads when the loader changes, showing the old data until the new answer', async () => {
    const answers = new Map([7, 30].map((days) => [days, deferred<string>()]))
    // A stable function per input, the way useCallback would hand them over.
    const loaders: Record<number, () => Promise<string>> = {}
    const loaderFor = (days: number) => (loaders[days] ??= () => answers.get(days)!.promise)
    const { result, rerender } = renderHook(({ days }) => useLoader(loaderFor(days)), {
      initialProps: { days: 7 },
    })

    await act(async () => answers.get(7)!.resolve('week'))
    expect(result.current.data).toBe('week')

    rerender({ days: 30 })
    expect(result.current).toMatchObject({ loading: true, data: 'week' })

    await act(async () => answers.get(30)!.resolve('month'))
    expect(result.current).toMatchObject({ loading: false, data: 'month' })
  })

  it('drops an answer that arrives after a newer request was made', async () => {
    const slow = deferred<string>()
    const fast = deferred<string>()
    const loadSlow = () => slow.promise
    const loadFast = () => fast.promise
    const { result, rerender } = renderHook(({ load }) => useLoader(load), {
      initialProps: { load: loadSlow },
    })

    rerender({ load: loadFast })
    await act(async () => fast.resolve('newer'))
    await act(async () => slow.resolve('older'))

    expect(result.current).toMatchObject({ loading: false, data: 'newer' })
  })

  it('lets an action replace the data without a request', async () => {
    let calls = 0
    const load = () => Promise.resolve(++calls)
    const { result } = renderHook(() => useLoader(load))
    await waitFor(() => expect(result.current.data).toBe(1))

    act(() => result.current.setData((n) => (n ?? 0) + 100))
    expect(result.current).toMatchObject({ loading: false, data: 101 })
    expect(calls).toBe(1)
  })

  it('loads nothing for a null loader', async () => {
    const { result } = renderHook(() => useLoader<string>(null))
    expect(result.current).toMatchObject({ loading: false, data: undefined, error: null })
    await expect(result.current.reload()).resolves.toBeUndefined()
  })
})
