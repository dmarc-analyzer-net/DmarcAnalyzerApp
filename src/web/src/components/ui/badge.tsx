import { cva, type VariantProps } from 'class-variance-authority'
import type * as React from 'react'

import { cn } from '@/lib/utils'

/**
 * Status pill. `dot` prepends a small status dot. `default`/`brand` and
 * `muted`/`neutral` are kept as aliases so existing pages keep compiling.
 */
const badgeVariants = cva(
  'inline-flex items-center gap-1.5 whitespace-nowrap rounded-pill px-2.5 py-0.5 font-body text-xs font-semibold leading-[18px]',
  {
    variants: {
      variant: {
        brand: 'bg-brand-subtle text-teal-800',
        default: 'bg-brand-subtle text-teal-800',
        success: 'bg-(--status-ok-bg) text-(--status-ok-fg)',
        warning: 'bg-(--status-warn-bg) text-(--status-warn-fg)',
        danger: 'bg-(--status-danger-bg) text-(--status-danger-fg)',
        neutral: 'bg-(--status-neutral-bg) text-(--status-neutral-fg)',
        muted: 'bg-(--status-neutral-bg) text-(--status-neutral-fg)',
      },
    },
    defaultVariants: {
      variant: 'neutral',
    },
  },
)

const DOT_COLOR: Record<NonNullable<VariantProps<typeof badgeVariants>['variant']>, string> = {
  brand: 'var(--status-ok-dot)',
  default: 'var(--status-ok-dot)',
  success: 'var(--status-ok-dot)',
  warning: 'var(--status-warn-dot)',
  danger: 'var(--status-danger-dot)',
  neutral: 'var(--status-neutral-dot)',
  muted: 'var(--status-neutral-dot)',
}

type BadgeProps = React.ComponentProps<'span'> &
  VariantProps<typeof badgeVariants> & {
    dot?: boolean
  }

export function Badge({ className, variant, dot = false, children, ...props }: BadgeProps) {
  return (
    <span className={cn(badgeVariants({ variant }), className)} {...props}>
      {dot ? (
        <span
          className="h-[7px] w-[7px] shrink-0 rounded-full"
          style={{ background: DOT_COLOR[variant ?? 'neutral'] }}
        />
      ) : null}
      {children}
    </span>
  )
}
