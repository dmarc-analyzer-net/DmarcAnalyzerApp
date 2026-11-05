import { describe, expect, it } from 'vitest'

import { enforcementEvidenceLine } from '@/lib/enforcement'
import type { EnforcementEvidence } from '@/lib/analytics'

const evidence = (over: Partial<EnforcementEvidence> = {}): EnforcementEvidence => ({
  messages: 4,
  sources: 1,
  reports: 2,
  daysWithData: 2,
  firstSeenUtc: '2026-11-01T00:00:00Z',
  lastSeenUtc: '2026-11-02T00:00:00Z',
  ...over,
})

/**
 * The ask behind this line: "a recommendation on thin data reads the same as one on
 * a month of traffic, and a copy button makes it easier to act on". So the numbers
 * have to be in the sentence, not implied by it.
 */
describe('enforcementEvidenceLine', () => {
  it('names the volume, sources, reports and days the verdict rests on', () => {
    expect(enforcementEvidenceLine(evidence())).toBe(
      'Based on 4 messages from 1 source across 2 reports over 2 days with data (Nov 1 to Nov 2).',
    )
  })

  it('singularises a single message, source, report and day', () => {
    expect(
      enforcementEvidenceLine(
        evidence({ messages: 1, sources: 1, reports: 1, daysWithData: 1, lastSeenUtc: '2026-11-01T00:00:00Z' }),
      ),
    ).toBe('Based on 1 message from 1 source across 1 report over 1 day with data (Nov 1 to Nov 1).')
  })

  it('compacts a large volume rather than printing every digit', () => {
    expect(enforcementEvidenceLine(evidence({ messages: 1_240_000 }))).toContain('1.2M messages')
  })

  it('says so plainly when the window holds no report data at all', () => {
    expect(
      enforcementEvidenceLine(
        evidence({
          messages: 0,
          sources: 0,
          reports: 0,
          daysWithData: 0,
          firstSeenUtc: null,
          lastSeenUtc: null,
        }),
      ),
    ).toBe('Based on no report data in this window.')
  })

  it('omits the date span when the window has no first or last seen', () => {
    const line = enforcementEvidenceLine(evidence({ firstSeenUtc: null, lastSeenUtc: null }))

    expect(line).toBe('Based on 4 messages from 1 source across 2 reports over 2 days with data.')
  })
})
