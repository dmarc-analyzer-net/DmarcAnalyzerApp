import { ComplianceSplitBar } from '@/components/data/ComplianceSplitBar'
import { SegmentBar } from '@/components/data/SegmentBar'
import { Card } from '@/components/ui/card'
import {
  ENFORCEMENT_STATUS_META,
  ENFORCEMENT_STATUS_ORDER,
  type EnforcementStatus,
} from '@/lib/analytics'
import { postureSegments, type PostureTotals } from '@/lib/bars'
import { formatCompact } from '@/lib/format'
import { cn } from '@/lib/utils'

type PosturePanelProps = {
  totals: PostureTotals
  /** The posture currently filtering the table, or null for "all". */
  active: EnforcementStatus | null
  /** Called with the clicked posture, or null when the active chip is clicked again. */
  onSelect: (status: EnforcementStatus | null) => void
  /** Window length in days, for the volume label. */
  days: number
  className?: string
}

/**
 * The portfolio view above the Domains table: how the domains in scope divide by
 * enforcement posture, and what share of their mail aligns.
 *
 * The bar counts **domains**, the compliance line counts **messages** — two
 * different denominators, which is why they are labelled rather than stacked
 * together. A single high-volume domain at 40% drags the message line down while
 * the domain bar still reads mostly green, and that difference is the point.
 *
 * Each legend entry is a filter toggle. The bar itself deliberately reflects the
 * client/search/policy filters but *not* the posture filter — otherwise selecting
 * a posture would collapse the bar to the one segment you just chose and hide the
 * portfolio you are navigating.
 */
export function PosturePanel({ totals, active, onSelect, days, className }: PosturePanelProps) {
  const segments = postureSegments(totals)
  const enforcing = totals.byStatus.enforced + totals.byStatus.ramping

  return (
    <Card className={cn('px-4 pt-4 pb-[18px] sm:px-5', className)}>
      <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <h2 className="font-display text-md font-bold tracking-tight text-body">
          Enforcement posture
        </h2>
        <p className="text-sm text-secondary">
          {totals.domains === 0
            ? 'No domains in scope'
            : `${enforcing} of ${totals.domains} ${
                totals.domains === 1 ? 'domain' : 'domains'
              } publish an enforcing policy`}
        </p>
      </div>

      {totals.domains === 0 ? (
        <p className="mt-2.5 text-sm text-secondary">
          Adjust the filters above to bring domains into scope.
        </p>
      ) : (
        <>
          <SegmentBar
            className="mt-3"
            segments={segments}
            height={10}
            label={`Domains by enforcement posture: ${segments
              .filter((segment) => segment.value > 0)
              .map((segment) => `${segment.value} ${segment.label}`)
              .join(', ')}`}
          />

          <div className="mt-2.5 flex flex-wrap gap-x-1.5 gap-y-1">
            {ENFORCEMENT_STATUS_ORDER.map((status) => {
              const meta = ENFORCEMENT_STATUS_META[status]
              const count = totals.byStatus[status]
              const selected = active === status
              return (
                <button
                  key={status}
                  type="button"
                  // Zero-count postures stay visible but unclickable: the absence of
                  // a Spoofing segment is information, and a chip that filters to an
                  // empty table is not.
                  disabled={count === 0}
                  aria-pressed={selected}
                  title={meta.description}
                  onClick={() => onSelect(selected ? null : status)}
                  className={cn(
                    'focus-ring inline-flex items-center gap-1.5 rounded-pill border px-2.5 py-0.5 text-xs transition-colors',
                    selected
                      ? 'border-brand bg-brand-subtle text-brand-strong'
                      : 'border-border text-secondary hover:border-border-strong',
                    count === 0 && 'cursor-default opacity-55 hover:border-border',
                  )}
                >
                  <span
                    className="h-2 w-2 shrink-0 rounded-[2px]"
                    style={{ background: meta.bar }}
                    aria-hidden
                  />
                  <span>{meta.label}</span>
                  <span className="font-semibold tabular-nums text-body">{count}</span>
                </button>
              )
            })}
          </div>

          <div className="mt-3.5 border-t border-border pt-3 sm:flex sm:items-center sm:justify-between sm:gap-4">
            <div className="text-sm text-secondary">
              <span className="font-mono text-body">{formatCompact(totals.messages)}</span> messages
              reported in the last {days} days
            </div>
            {totals.messages > 0 ? (
              <ComplianceSplitBar
                className="mt-2 w-full sm:mt-0 sm:w-[260px]"
                compliant={totals.compliantMessages}
                messages={totals.messages}
                showFailing
              />
            ) : null}
          </div>
        </>
      )}
    </Card>
  )
}
