import type * as React from 'react'

import { cn } from '@/lib/utils'

export function Table({ className, ...props }: React.ComponentProps<'table'>) {
  return (
    <table className={cn('w-full border-collapse font-body text-base', className)} {...props} />
  )
}

export function TableHeader({ className, ...props }: React.ComponentProps<'thead'>) {
  return <thead className={cn('bg-gray-50', className)} {...props} />
}

export function TableBody({ className, ...props }: React.ComponentProps<'tbody'>) {
  return <tbody className={cn('[&_tr:last-child]:border-0', className)} {...props} />
}

type TableRowProps = React.ComponentProps<'tr'> & {
  /** Drops the bottom divider (design's `last`). */
  last?: boolean
}

export function TableRow({ className, last, onClick, ...props }: TableRowProps) {
  return (
    <tr
      onClick={onClick}
      className={cn(
        'border-b border-[var(--gray-100)] transition-colors duration-[120ms] ease-out hover:bg-gray-50',
        last && 'border-0',
        onClick && 'cursor-pointer',
        className,
      )}
      {...props}
    />
  )
}

export function TableHead({ className, ...props }: React.ComponentProps<'th'>) {
  return (
    <th
      className={cn(
        'border-b border-border px-4 py-2.5 text-left text-xs font-semibold whitespace-nowrap text-secondary',
        className,
      )}
      {...props}
    />
  )
}

type TableCellProps = React.ComponentProps<'td'> & {
  mono?: boolean
  align?: 'left' | 'right' | 'center'
}

export function TableCell({ className, mono, align, ...props }: TableCellProps) {
  return (
    <td
      className={cn(
        'px-4 py-3 align-middle text-body',
        mono && 'font-mono text-sm',
        align === 'right' && 'text-right',
        align === 'center' && 'text-center',
        className,
      )}
      {...props}
    />
  )
}
