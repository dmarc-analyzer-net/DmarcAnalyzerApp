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
    <Card className={cn('px-4 pt-4 pb-[18px] sm:px-5', className)}>
      <div className="mb-2 text-sm text-secondary">{label}</div>
      {/* These sit two-up on a phone, so the value and its badge need somewhere
          to go rather than forcing the tile wider than its grid column. */}
      <div className="flex flex-wrap items-baseline gap-x-2.5 gap-y-1">
        {/* 28px overruns a half-width tile once the value reaches seven figures,
            which message volumes routinely do. */}
        <span className="font-display text-xl font-bold leading-none tracking-tight text-body sm:text-2xl">
          {value}
        </span>
        {typeof extra === 'string' ? <Badge variant="brand">{extra}</Badge> : extra}
      </div>
    </Card>
  )
}
