import type * as React from 'react'

import { Icon } from '@/components/ui/icon'
import { cn } from '@/lib/utils'

type SelectOption = string | { value: string; label: string }

type SelectProps = React.ComponentProps<'select'> & {
  /** Convenience options (design). Native `<option>` children are also supported. */
  options?: SelectOption[]
}

const selectBase =
  'h-9 w-full cursor-pointer appearance-none rounded-md border border-border bg-surface-card pl-3 pr-8 font-body text-base text-body outline-none focus:border-brand focus:shadow-[var(--focus-ring)] disabled:cursor-not-allowed disabled:opacity-50'

export function Select({ className, options, children, ...props }: SelectProps) {
  return (
    <span className={cn('relative inline-block w-full', className)}>
      <select className={selectBase} {...props}>
        {options?.map((option) => {
          const opt = typeof option === 'string' ? { value: option, label: option } : option
          return (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          )
        })}
        {children}
      </select>
      <span className="pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 text-faint">
        <Icon name="chevron-down" size={14} />
      </span>
    </span>
  )
}
