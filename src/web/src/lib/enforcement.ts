import type { EnforcementEvidence } from '@/lib/analytics'
import { formatCompact, formatShortDate } from '@/lib/format'

const plural = (count: number, noun: string) => `${formatCompact(count)} ${noun}${count === 1 ? '' : 's'}`

/**
 * One line saying what the recommendation was drawn from.
 *
 * Without it a verdict on four messages over two days reads exactly like one on a
 * month of traffic, and the copy button makes the thin one just as easy to act on.
 * Days are days that carry data, not the elapsed span, so a quiet fortnight between
 * two reports does not inflate the number — the dates alongside give the span.
 */
export function enforcementEvidenceLine(evidence: EnforcementEvidence): string {
  if (evidence.messages === 0) {
    return 'Based on no report data in this window.'
  }

  const parts = [
    `Based on ${plural(evidence.messages, 'message')}`,
    `from ${plural(evidence.sources, 'source')}`,
    `across ${plural(evidence.reports, 'report')}`,
    `over ${plural(evidence.daysWithData, 'day')} with data`,
  ]

  const span =
    evidence.firstSeenUtc && evidence.lastSeenUtc
      ? ` (${formatShortDate(evidence.firstSeenUtc)} to ${formatShortDate(evidence.lastSeenUtc)})`
      : ''

  return `${parts.join(' ')}${span}.`
}
