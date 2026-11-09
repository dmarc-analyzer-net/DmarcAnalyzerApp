import {
  ENFORCEMENT_STATUS_META,
  ENFORCEMENT_STATUS_ORDER,
  type EnforcementStatus,
} from '@/lib/analytics'

/**
 * Data for the stacked-bar visuals in `components/data`. Kept out of the component
 * files because a module that exports both a component and a helper loses Vite's
 * fast refresh (the `react-refresh/only-export-components` rule).
 */

export type BarSegment = {
  /** Stable key; also the legend label when `label` is omitted. */
  key: string
  label?: string
  value: number
  /** CSS colour — pass a design token (`var(--status-ok-dot)`), not a hex literal. */
  color: string
}

export const segmentLabel = (segment: BarSegment) => segment.label ?? segment.key

/**
 * The text that makes a stacked bar readable: "842 aligned, 61 failing".
 * Segments with no volume are dropped — a legend entry reading "0 rejected" is
 * noise on every healthy domain.
 */
export function describeSegments(segments: BarSegment[]): string {
  const parts = segments
    .filter((segment) => segment.value > 0)
    .map((segment) => `${segment.value.toLocaleString('en-US')} ${segmentLabel(segment)}`)
  return parts.length > 0 ? parts.join(', ') : 'nothing reported'
}

export type PostureTotals = {
  /** Domains in the bar, by posture. Every key present, zeroes included. */
  byStatus: Record<EnforcementStatus, number>
  domains: number
  messages: number
  compliantMessages: number
}

/** Rolls a set of domain rows up into the shape the posture panel renders. */
export function postureTotals(
  rows: readonly {
    enforcementStatus: EnforcementStatus
    messages: number
    compliantMessages: number
  }[],
): PostureTotals {
  const byStatus = Object.fromEntries(
    ENFORCEMENT_STATUS_ORDER.map((status) => [status, 0]),
  ) as Record<EnforcementStatus, number>

  let messages = 0
  let compliantMessages = 0
  for (const row of rows) {
    byStatus[row.enforcementStatus] += 1
    messages += row.messages
    compliantMessages += row.compliantMessages
  }

  return { byStatus, domains: rows.length, messages, compliantMessages }
}

/** The posture bar's segments, in {@link ENFORCEMENT_STATUS_ORDER}. */
export function postureSegments(totals: PostureTotals): BarSegment[] {
  return ENFORCEMENT_STATUS_ORDER.map((status) => ({
    key: status,
    label: ENFORCEMENT_STATUS_META[status].label,
    value: totals.byStatus[status],
    color: ENFORCEMENT_STATUS_META[status].bar,
  }))
}
