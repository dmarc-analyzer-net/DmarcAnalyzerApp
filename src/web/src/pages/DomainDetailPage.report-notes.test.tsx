import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import type { ReportNote, ReportNotes } from '@/lib/analytics'
import { reportNotesSummary } from '@/lib/report-notes'
import { ReportNotesList } from '@/pages/DomainDetailPage'

/**
 * The report-notes panel (PR 2 of docs/planning/rfc-9990-plan.md). The list has to be
 * honest about its denominators — the cap, info-only reports it was not asked for, and
 * reports stored before notes were kept, which are unknown rather than clean — and a
 * backfilled row has to say its notes come from a later parser.
 */

function note(overrides: Partial<ReportNote> = {}): ReportNote {
  return {
    id: 'r1',
    organizationName: 'reporter.example',
    reportId: 'abc-123',
    rangeBeginUtc: '2026-10-01T00:00:00Z',
    rangeEndUtc: '2026-10-02T00:00:00Z',
    ingestedAtUtc: '2026-10-02T01:00:00Z',
    origin: 'ingest',
    warningCount: 1,
    messages: [
      { severity: 'info', text: "stripped XML namespace 'urn:ietf:params:xml:ns:dmarc-2.0' for compatibility" },
      { severity: 'warning', text: "replaced unrecognised policy_evaluated/dkim value 'x' with 'fail' for compatibility" },
    ],
    ...overrides,
  }
}

function notes(overrides: Partial<ReportNotes> = {}): ReportNotes {
  return {
    window: { days: 30, beginUtc: '2026-09-02T00:00:00Z', endUtc: '2026-10-02T00:00:00Z', anchoredToLatestData: true },
    includeInfo: false,
    limit: 100,
    reportsInWindow: 12,
    reportsWithWarnings: 1,
    reportsWithInfoOnly: 0,
    reportsNotRecorded: 0,
    reports: [note()],
    ...overrides,
  }
}

describe('reportNotesSummary', () => {
  it('states the warnings against the window', () => {
    expect(reportNotesSummary(notes())).toBe('1 of 12 reports in the window has warnings.')
  })

  it('says how many reports it cannot speak for, and how many are info-only', () => {
    const three = [note({ id: 'a' }), note({ id: 'b' }), note({ id: 'c' })]
    expect(
      reportNotesSummary(
        notes({ reportsWithWarnings: 3, reportsWithInfoOnly: 4, reportsNotRecorded: 5, reports: three }),
      ),
    ).toBe(
      '3 of 12 reports in the window have warnings; 4 have informational notes only; 5 were stored before notes were recorded and cannot be shown.',
    )
  })

  it('says when the list is cut by the cap', () => {
    const many = Array.from({ length: 100 }, (_, i) => note({ id: `r${i}` }))
    expect(reportNotesSummary(notes({ reportsInWindow: 500, reportsWithWarnings: 150, reports: many }))).toBe(
      '150 of 500 reports in the window have warnings — showing the newest 100.',
    )
  })

  it('does not count info-only reports as cut when they were not asked for', () => {
    expect(reportNotesSummary(notes({ reportsWithInfoOnly: 9 }))).toBe(
      '1 of 12 reports in the window has warnings; 9 have informational notes only.',
    )
  })
})

describe('ReportNotesList', () => {
  it('lists each report collapsed, and expands to its messages', () => {
    render(<ReportNotesList notes={notes()} />)

    const row = screen.getByRole('button', { name: /reporter\.example/ })
    expect(row).toHaveAttribute('aria-expanded', 'false')
    expect(row).toHaveTextContent('1 warning')
    expect(screen.queryByText(/stripped XML namespace/)).not.toBeInTheDocument()

    fireEvent.click(row)

    expect(row).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText(/stripped XML namespace/)).toBeInTheDocument()
    expect(screen.getByText(/replaced unrecognised policy_evaluated\/dkim/)).toBeInTheDocument()
    expect(screen.getByText('report id abc-123')).toBeInTheDocument()
  })

  it('marks a backfilled row as re-parsed, since its notes come from a later parser', () => {
    render(<ReportNotesList notes={notes({ reports: [note({ origin: 'backfill' })] })} />)

    expect(screen.getByText('re-parsed later')).toBeInTheDocument()
  })

  it('counts notes rather than warnings for an info-only row', () => {
    render(
      <ReportNotesList
        notes={notes({
          includeInfo: true,
          reports: [note({ warningCount: 0, messages: [{ severity: 'info', text: 'how it was read' }] })],
        })}
      />,
    )

    expect(screen.getByRole('button')).toHaveTextContent('1 note')
  })
})
