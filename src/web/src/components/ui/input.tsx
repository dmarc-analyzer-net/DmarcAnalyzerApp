import type * as React from 'react'

import { Icon, type IconName } from '@/components/ui/icon'
import { cn } from '@/lib/utils'

type InputProps = React.ComponentProps<'input'> & {
  /** Optional leading icon (kebab name). */
  icon?: IconName
  /** Render the value in the mono type family. */
  mono?: boolean
}

// The 16px mobile type size is not a taste call: iOS Safari zooms the whole page
// when a focused field is under 16px, and the app's 14px `text-base` triggers it
// on every input. Taller control below sm for the touch target; both revert at sm.
const base =
  'h-10 w-full rounded-md border border-border bg-surface-card px-3 font-body text-[16px] text-body outline-none transition-[box-shadow,border-color] duration-[120ms] ease-out placeholder:text-faint focus:border-brand focus:shadow-[var(--focus-ring)] disabled:cursor-not-allowed disabled:opacity-50 sm:h-9 sm:text-base'

export function Input({ className, icon, mono, ...props }: InputProps) {
  const field = (
    <input className={cn(base, icon && 'pl-[34px]', mono && 'font-mono', className)} {...props} />
  )
  if (!icon) return field
  return (
    <span className="relative block w-full">
      <span className="pointer-events-none absolute left-[11px] top-1/2 -translate-y-1/2 text-faint">
        <Icon name={icon} size={15} />
      </span>
      {field}
    </span>
  )
}
