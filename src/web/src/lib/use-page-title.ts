import { useEffect } from 'react'

/** Sets the browser tab title for the page's lifetime, restoring the previous value on unmount. */
export function usePageTitle(title: string): void {
  useEffect(() => {
    const previous = document.title
    document.title = `${title} · DMARC Analyzer`
    return () => {
      document.title = previous
    }
  }, [title])
}
