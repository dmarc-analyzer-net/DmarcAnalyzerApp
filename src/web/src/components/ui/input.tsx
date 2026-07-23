import type * as React from 'react'

import { Icon, type IconName } from '@/components/ui/icon'
import { cn } from '@/lib/utils'

type InputProps = React.ComponentProps<'input'> & {
  /** Optional leading icon (kebab name). */
  icon?: IconName
  /** Render the value in the mono type family. */
  mono?: boolean
}

const base =
  'h-9 w-full rounded-md border border-border bg-surface-card px-3 font-body text-base text-body outline-none transition-[box-shadow,border-color] duration-[120ms] ease-out placeholder:text-faint focus:border-brand focus:shadow-[var(--focus-ring)] disabled:cursor-not-allowed disabled:opacity-50'

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
