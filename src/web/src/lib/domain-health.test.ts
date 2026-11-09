import { describe, expect, it } from 'vitest'

import type { DrilldownTotals } from '@/lib/analytics'
import { describeSegments } from '@/lib/bars'
import { describeAlignmentCarrier, dispositionBreakdown } from '@/lib/domain-health'

function totals(overrides: Partial<DrilldownTotals> = {}): DrilldownTotals {
  return {
    messages: 1000,
    compliantMessages: 990,
    complianceRate: 0.99,
    dkimPassRate: 0.98,
    spfPassRate: 0.4,
    reports: 20,
    sources: 6,
    reporters: 3,
    quarantined: 0,
    rejected: 0,
    status: 'aligned',
    ...overrides,
  }
}

/**
 * Reports carry quarantine and reject counts but no "delivered" count, so the
 * delivered segment is the remainder. These assert the remainder accounts for
 * every message the tile above it claims — the failure mode is a breakdown that
 * silently sums to less than the total.
 */
describe('dispositionBreakdown', () => {
  it('derives delivered as the remainder of the window total', () => {
    const segments = dispositionBreakdown(totals({ messages: 1000 }))

    expect(segments.reduce((acc, s) => acc + s.value, 0)).toBe(1000)
    expect(segments.find((s) => s.key === 'delivered')?.value).toBe(1000)
  })

  it('splits quarantined and rejected out of the delivered remainder', () => {
    const segments = dispositionBreakdown(totals({ messages: 1000, quarantined: 40, rejected: 10 }))

    expect(segments.find((s) => s.key === 'delivered')?.value).toBe(950)
    expect(segments.find((s) => s.key === 'quarantined')?.value).toBe(40)
    expect(segments.find((s) => s.key === 'rejected')?.value).toBe(10)
    expect(describeSegments(segments)).toBe('950 delivered, 40 quarantined, 10 rejected')
  })

  it('never produces a negative delivered share when the counts exceed the total', () => {
    // Different reporters can double-count a message across overlapping ranges; a
    // negative segment would render as a bar wider than its track.
    const segments = dispositionBreakdown(totals({ messages: 10, quarantined: 8, rejected: 7 }))

    expect(segments.find((s) => s.key === 'delivered')?.value).toBe(0)
  })

  it('reads as nothing reported for a silent window rather than all delivered', () => {
    const segments = dispositionBreakdown(
      totals({ messages: 0, compliantMessages: 0, quarantined: 0, rejected: 0 }),
    )

    expect(describeSegments(segments)).toBe('nothing reported')
  })

  it('tolerates absent totals while the drill-down is still loading', () => {
    expect(dispositionBreakdown(undefined).every((s) => s.value === 0)).toBe(true)
  })
})

/**
 * DMARC passes when either mechanism aligns, so the headline names the one that is
 * actually holding the domain up. Naming the wrong one would tell an operator that
 * a forwarding path (which breaks SPF and not DKIM) is cheap when it is not.
 */
describe('describeAlignmentCarrier', () => {
  it('names DKIM when it out-aligns SPF', () => {
    expect(describeAlignmentCarrier(totals({ dkimPassRate: 0.98, spfPassRate: 0.4 }))).toEqual({
      rate: 0.98,
      label: 'DKIM',
    })
  })

  it('names SPF when it out-aligns DKIM', () => {
    expect(describeAlignmentCarrier(totals({ dkimPassRate: 0.1, spfPassRate: 0.97 }))).toEqual({
      rate: 0.97,
      label: 'SPF',
    })
  })

  it('names both when the two rates are identical', () => {
    expect(describeAlignmentCarrier(totals({ dkimPassRate: 1, spfPassRate: 1 }))).toEqual({
      rate: 1,
      label: 'DKIM & SPF',
    })
  })

  it('does not claim a carrier before the drill-down has loaded', () => {
    expect(describeAlignmentCarrier(undefined).rate).toBe(0)
  })
})
