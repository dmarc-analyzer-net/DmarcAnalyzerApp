import type * as React from 'react'

import { cn } from '@/lib/utils'

type CardProps = React.ComponentProps<'div'> & {
  /** Applies the design's 20px inset. Off by default so composed
   * `CardHeader`/`CardContent` sections own their own padding. */
  pad?: boolean
}

export function Card({ className, pad = false, ...props }: CardProps) {
  return (
    <div
      className={cn(
        'rounded-lg border border-border bg-surface-card shadow-card',
        pad && 'p-5',
        className,
      )}
      {...props}
    />
  )
}

type CardHeaderProps = React.ComponentProps<'div'> & {
  /** Prop-driven header (design). When omitted the header renders `children`
   * so the composed `CardTitle`/`CardDescription` form keeps working. */
  title?: React.ReactNode
  description?: React.ReactNode
  actions?: React.ReactNode
}

export function CardHeader({
  className,
  title,
  description,
  actions,
  children,
  ...props
}: CardHeaderProps) {
  if (title != null || description != null || actions != null) {
    return (
      <div className={cn('mb-3.5 flex items-start justify-between gap-3', className)} {...props}>
        <div>
          <h3 className="font-display text-md font-bold tracking-tight text-body">{title}</h3>
          {description != null ? (
            <p className="mt-[3px] text-sm text-secondary">{description}</p>
          ) : null}
        </div>
        {actions != null ? <div className="flex shrink-0 gap-2">{actions}</div> : null}
      </div>
    )
  }
  return (
    <div className={cn('flex items-start justify-between px-5 pt-5', className)} {...props}>
      {children}
    </div>
  )
}

export function CardTitle({ className, ...props }: React.ComponentProps<'h3'>) {
  return (
    <h3
      className={cn('font-display text-md font-bold tracking-tight text-body', className)}
      {...props}
    />
  )
}

export function CardDescription({ className, ...props }: React.ComponentProps<'p'>) {
  return <p className={cn('text-sm text-secondary', className)} {...props} />
}

export function CardContent({ className, ...props }: React.ComponentProps<'div'>) {
  return <div className={cn('px-5 pb-5', className)} {...props} />
}
