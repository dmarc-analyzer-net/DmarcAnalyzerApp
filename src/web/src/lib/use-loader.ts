import { useCallback, useEffect, useRef, useState } from 'react'

export interface Loader<T> {
  /**
   * The newest successful result. Kept while a reload or a changed loader is in
   * flight, so a page does not blank out between a filter change and its answer;
   * a caller that must not show the previous answer checks `loading` first.
   */
  data: T | undefined
  /**
   * What the current load threw, or null. Only ever the current one: a request
   * that is still in flight has no error yet, so a reload clears the last one the
   * moment it starts rather than leaving it on screen beside a spinner.
   */
  error: unknown
  /** True from the first render until the current loader has answered. */
  loading: boolean
  /**
   * Fetch again, and resolve with the result once it lands — undefined if it
   * failed, since the failure is already in `error`. For event handlers, after a
   * mutation; never needed to follow a change of loader, which reloads by itself.
   */
  reload: () => Promise<T | undefined>
  /**
   * Replace the loaded value without a request, for an action whose response is
   * itself the new state (a recheck, a poll, a queued run).
   */
  setData: (update: (current: T | undefined) => T | undefined) => void
}

interface Settled<T> {
  load: (() => Promise<T>) | null
  version: number
  data: T | undefined
  error: unknown
}

/**
 * Loads data for a component, and reloads whenever `load` changes identity — so
 * wrap it in `useCallback` and list what the request depends on. Pass null to
 * load nothing yet.
 *
 * Whether a load is in flight is derived rather than stored: the state remembers
 * which loader (and which reload) produced it, and anything newer is loading.
 * That is the point of the hook. The obvious version — set a busy flag, clear the
 * error, then fetch, all from an effect — sets state synchronously inside the
 * effect and costs every page load an extra render, which React's compiler lint
 * (`react-hooks/set-state-in-effect`) refuses. Here the effect only ever sets
 * state from the request's own callbacks.
 *
 * A request superseded by a newer one is discarded, so an answer that arrives out
 * of order cannot overwrite a newer one.
 */
export function useLoader<T>(load: (() => Promise<T>) | null): Loader<T> {
  const [version, setVersion] = useState(0)
  const [settled, setSettled] = useState<Settled<T>>({
    load: null,
    version: -1,
    data: undefined,
    error: null,
  })

  /** Callers awaiting a reload, each released once its version (or a newer one) settles. */
  const waiters = useRef<Array<{ version: number; resolve: (data: T | undefined) => void }>>([])

  useEffect(() => {
    if (load === null) return
    let current = true

    const release = (data: T | undefined) => {
      const ready = waiters.current.filter((w) => w.version <= version)
      waiters.current = waiters.current.filter((w) => w.version > version)
      for (const waiter of ready) waiter.resolve(data)
    }

    load().then(
      (data) => {
        if (!current) return
        setSettled({ load, version, data, error: null })
        release(data)
      },
      (error: unknown) => {
        if (!current) return
        setSettled((previous) => ({ load, version, data: previous.data, error }))
        release(undefined)
      },
    )

    return () => {
      current = false
    }
  }, [load, version])

  const reload = useCallback((): Promise<T | undefined> => {
    if (load === null) return Promise.resolve(undefined)
    const next = version + 1
    setVersion(next)
    return new Promise((resolve) => {
      waiters.current.push({ version: next, resolve })
    })
  }, [load, version])

  const setData = useCallback((update: (current: T | undefined) => T | undefined) => {
    setSettled((previous) => ({ ...previous, data: update(previous.data) }))
  }, [])

  const current = settled.load === load && settled.version === version
  return {
    data: settled.data,
    error: current ? settled.error : null,
    loading: load !== null && !current,
    reload,
    setData,
  }
}

/** The message to show for a failed load, or null when there is nothing to show. */
export function loadErrorMessage(error: unknown, fallback: string): string | null {
  if (error === null || error === undefined) return null
  return error instanceof Error ? error.message : fallback
}
