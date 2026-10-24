import { useEffect } from 'react'

import { useBranding } from '@/lib/branding'

/**
 * Sets the browser tab title for the page's lifetime, restoring the previous value on
 * unmount. Suffixed with the instance's brand name once it is known, and bare until then.
 */
export function usePageTitle(title: string): void {
  const name = useBranding()?.name

  useEffect(() => {
    const previous = document.title
    document.title = name ? `${title} · ${name}` : title
    return () => {
      document.title = previous
    }
  }, [title, name])
}
