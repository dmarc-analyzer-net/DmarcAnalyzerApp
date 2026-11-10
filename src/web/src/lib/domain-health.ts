import type { DrilldownTotals } from '@/lib/analytics'
import type { BarSegment } from '@/lib/bars'

/**
 * Derivations behind the domain detail page's health tiles. Pure, and out of the
 * page file so they can be tested directly — a module that exports both a
 * component and a helper loses Vite's fast refresh.
 */

/**
 * The three receiver actions a window's mail met, as bar segments.
 *
 * Reports only carry quarantine and reject counts, so "delivered" is the
 * remainder — which is correct, but worth saying out loud: it covers both mail
 * that passed and mail that failed under a policy too weak to act on it. The
 * colours read as escalating *receiver action*, not as a verdict: a rejected
 * spoof is a good outcome and a delivered failure is a bad one, so neither tone
 * is claiming otherwise.
 */
export function dispositionBreakdown(totals: DrilldownTotals | undefined): BarSegment[] {
  const quarantined = totals?.quarantined ?? 0
  const rejected = totals?.rejected ?? 0
  const delivered = Math.max(0, (totals?.messages ?? 0) - quarantined - rejected)
  return [
    { key: 'delivered', label: 'delivered', value: delivered, color: 'var(--brand)' },
    { key: 'quarantined', label: 'quarantined', value: quarantined, color: 'var(--status-warn-dot)' },
    { key: 'rejected', label: 'rejected', value: rejected, color: 'var(--status-danger-dot)' },
  ]
}

/**
 * Which mechanism is carrying DMARC alignment, and at what rate. DMARC passes on
 * either, so the higher rate is the one holding the domain up — and knowing which
 * it is tells an operator what a forwarding path (which breaks SPF but not DKIM)
 * will cost them.
 */
export function describeAlignmentCarrier(totals: DrilldownTotals | undefined): {
  rate: number
  label: string
} {
  const dkim = totals?.dkimPassRate ?? 0
  const spf = totals?.spfPassRate ?? 0
  if (dkim === spf) return { rate: dkim, label: 'DKIM & SPF' }
  return dkim > spf ? { rate: dkim, label: 'DKIM' } : { rate: spf, label: 'SPF' }
}
