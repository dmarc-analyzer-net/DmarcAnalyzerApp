import type { ReactNode } from 'react'

/** One label/value line in a status card: label left, value and badge right, hint below. */
export function StatusRow({
  label,
  badge,
  value,
  hint,
}: {
  label: string
  badge?: ReactNode
  value?: string
  hint?: ReactNode
}) {
  return (
    <div className="border-t border-border py-2.5 first:border-t-0 first:pt-0 last:pb-0">
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
        <span className="text-sm text-secondary">{label}</span>
        <span className="flex items-center gap-2 text-sm text-body">
          {value != null ? <span>{value}</span> : null}
          {badge}
        </span>
      </div>
      {hint != null ? <p className="mt-1 text-xs text-faint">{hint}</p> : null}
    </div>
  )
}
