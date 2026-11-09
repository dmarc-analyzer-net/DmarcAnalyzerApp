import { describe, expect, it } from 'vitest'

import { ENFORCEMENT_STATUS_ORDER, type EnforcementStatus } from '@/lib/analytics'
import { describeSegments, postureSegments, postureTotals } from '@/lib/bars'

const row = (enforcementStatus: EnforcementStatus, messages = 0, compliantMessages = 0) => ({
  enforcementStatus,
  messages,
  compliantMessages,
})

describe('postureTotals', () => {
  it('keeps a zero entry for every posture so absent ones stay legible', () => {
    const totals = postureTotals([row('enforced', 100, 100)])

    expect(Object.keys(totals.byStatus).sort()).toEqual([...ENFORCEMENT_STATUS_ORDER].sort())
    expect(totals.byStatus.spoofing).toBe(0)
  })

  it('counts domains by posture and sums message volume independently', () => {
    const totals = postureTotals([
      row('enforced', 1000, 1000),
      row('spoofing', 500, 200),
      row('spoofing', 10, 0),
      // A no_data domain contributes to the domain count but nothing to the volume,
      // which is what lets the panel show "4 domains" over "1.5k messages".
      row('no_data'),
    ])

    expect(totals.domains).toBe(4)
    expect(totals.byStatus.spoofing).toBe(2)
    expect(totals.byStatus.no_data).toBe(1)
    expect(totals.messages).toBe(1510)
    expect(totals.compliantMessages).toBe(1200)
  })

  it('reports an empty portfolio rather than throwing on no rows', () => {
    const totals = postureTotals([])

    expect(totals.domains).toBe(0)
    expect(totals.messages).toBe(0)
    expect(totals.byStatus.enforced).toBe(0)
  })
})

describe('postureSegments', () => {
  it('gives no_data a fill of its own, so it cannot read as monitoring', () => {
    const segments = postureSegments(postureTotals([row('monitoring', 5, 5), row('no_data')]))
    const monitoring = segments.find((s) => s.key === 'monitoring')
    const noData = segments.find((s) => s.key === 'no_data')

    expect(monitoring?.color).not.toBe(noData?.color)
  })
})

describe('describeSegments', () => {
  it('drops empty buckets so a healthy domain does not read "0 rejected"', () => {
    expect(
      describeSegments([
        { key: 'delivered', label: 'delivered', value: 1234, color: 'teal' },
        { key: 'quarantined', label: 'quarantined', value: 0, color: 'amber' },
        { key: 'rejected', label: 'rejected', value: 7, color: 'red' },
      ]),
    ).toBe('1,234 delivered, 7 rejected')
  })

  it('says nothing was reported rather than returning an empty string', () => {
    expect(describeSegments([{ key: 'delivered', value: 0, color: 'teal' }])).toBe(
      'nothing reported',
    )
  })
})
