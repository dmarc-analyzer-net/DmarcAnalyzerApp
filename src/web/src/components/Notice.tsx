import type { ReactNode } from 'react'

import { Icon, type IconName } from '@/components/ui/icon'
import { cn } from '@/lib/utils'

/**
 * The inline banner the console uses instead of a toast — errors, warnings and
 * confirmations that must stay on screen until the operator has read them.
 *
 * It exists as a component rather than as repeated markup because it is the primary
 * feedback channel for the backup pages, where three files would otherwise each
 * carry the same `var(--status-*)` strings. That is how a hard-coded colour
 * eventually gets typed by hand. Deliberately not in `components/ui`: the design
 * system's primitive set is closed, and this is the existing pattern extracted, not
 * a new primitive.
 */

type NoticeTone = 'ok' | 'warn' | 'danger'

const TONE: Record<NoticeTone, { surface: string; icon: IconName }> = {
  ok: {
    surface:
      'border-[var(--status-ok-bg)] bg-[var(--status-ok-bg)] text-[var(--status-ok-fg)]',
    icon: 'circle-check',
  },
  warn: {
    surface:
      'border-[var(--status-warn-bg)] bg-[var(--status-warn-bg)] text-[var(--status-warn-fg)]',
    icon: 'triangle-alert',
  },
  danger: {
    surface:
      'border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] text-[var(--status-danger-fg)]',
    icon: 'circle-alert',
  },
}

type NoticeProps = {
  tone: NoticeTone
  /** Optional bold first line, for a notice whose body needs more than one sentence. */
  title?: ReactNode
  children: ReactNode
  className?: string
}

export function Notice({ tone, title, children, className }: NoticeProps) {
  const { surface, icon } = TONE[tone]
  return (
    <div
      // `alert` only for the danger tone: it interrupts a screen reader, which is
      // right for "your mailbox passwords are in plaintext" and wrong for a routine
      // confirmation.
      role={tone === 'danger' ? 'alert' : undefined}
      className={cn('rounded-md border px-3 py-2.5 text-sm', surface, className)}
    >
      <div className="flex items-start gap-2">
        <span className="mt-[3px] shrink-0">
          <Icon name={icon} size={15} />
        </span>
        <div className="min-w-0 flex-1">
          {title != null ? <p className="font-semibold">{title}</p> : null}
          <div className={cn(title != null && 'mt-0.5')}>{children}</div>
        </div>
      </div>
    </div>
  )
}
