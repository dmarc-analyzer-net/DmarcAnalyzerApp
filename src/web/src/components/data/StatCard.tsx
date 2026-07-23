import type * as React from 'react'

import { Badge } from '@/components/ui/badge'
import { Card } from '@/components/ui/card'
import { cn } from '@/lib/utils'

type StatCardProps = {
  label: React.ReactNode
  value: React.ReactNode
  /** Trailing badge or delta. A plain string renders as a brand badge. */
  extra?: React.ReactNode
  className?: string
}

/** Dashboard metric: small secondary label over a large display number. */
export function StatCard({ label, value, extra, className }: StatCardProps) {
  return (
    <Card className={cn('px-5 pt-4 pb-[18px]', className)}>
      <div className="mb-2 text-sm text-secondary">{label}</div>
      <div className="flex items-baseline gap-2.5">
        <span className="font-display text-2xl font-bold leading-none tracking-tight text-body">
          {value}
        </span>
        {typeof extra === 'string' ? <Badge variant="brand">{extra}</Badge> : extra}
      </div>
    </Card>
  )
}
