import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { ReportBackfillCard } from '@/components/ReportBackfillCard'
import type { ReportBackfillPreview, ReportBackfillState } from '@/lib/entities'
import { describeBackfillCounts, describeReportsWithoutFields } from '@/lib/report-backfill'

// Hoisted with the mock: a plain module-scope vi.fn() is not initialised yet when the
// mocked module is first imported, and the card's calls would hit an empty function.
const { fetchJson } = vi.hoisted(() => ({ fetchJson: vi.fn() }))
vi.mock('@/lib/api', () => ({ fetchJson }))

function preview(overrides: Partial<ReportBackfillPreview> = {}): ReportBackfillPreview {
  return {
    archiveConfigured: true,
    archive: 's3://backup (eu-west-1)',
    prefix: 'dmarc/reports/',
    archivedObjects: 1200,
    oldestArchivedDay: '2026-08-01',
    newestArchivedDay: '2026-10-12',
    reportsNotRecorded: 500,
    reportsFromPushedSources: 20,
    reportsFromArchivableSources: 480,
    reportsIngestedBeforeTheArchive: 100,
    estimatedToStayUnrecorded: 120,
    ...overrides,
  }
}

function state(overrides: Partial<ReportBackfillState> = {}): ReportBackfillState {
  return {
    status: 'idle',
    pass: null,
    lastObjectKey: null,
    requestedAtUtc: null,
    startedAtUtc: null,
    finishedAtUtc: null,
    objectsSeen: 0,
    payloadsParsed: 0,
    matched: 0,
    updated: 0,
    skipped: 0,
    ambiguous: 0,
    failed: 0,
    lastError: null,
    ...overrides,
  }
}

function answer(p: ReportBackfillPreview, s: ReportBackfillState) {
  fetchJson.mockImplementation((url: string, init?: RequestInit) => {
    if (url.endsWith('/preview')) return Promise.resolve(p)
    if (url.endsWith('/status')) return Promise.resolve(s)
    if (url.endsWith('/run') && init?.method === 'POST') {
      return Promise.resolve(state({ status: 'requested', requestedAtUtc: '2026-10-13T12:00:00Z' }))
    }
    return Promise.reject(new Error(`unexpected ${url}`))
  })
}

describe('describeBackfillCounts', () => {
  it('names every count in the order it happens', () => {
    expect(
      describeBackfillCounts(
        state({ objectsSeen: 1200, payloadsParsed: 1300, matched: 400, updated: 395, skipped: 900, ambiguous: 5, failed: 2 }),
      ),
    ).toBe('1,200 archived mails · 1,300 reports · 400 matched · 395 updated · 900 skipped · 5 ambiguous · 2 failed')
  })
})

describe('describeReportsWithoutFields', () => {
  it('says every report has them when none is missing', () => {
    expect(describeReportsWithoutFields(preview({ reportsNotRecorded: 0 }))).toBe('Every stored report already has them.')
  })

  it('without an archive, says none can be filled instead of describing an archive that does not exist', () => {
    const text = describeReportsWithoutFields(
      preview({
        archiveConfigured: false,
        archive: null,
        archivedObjects: 0,
        oldestArchivedDay: null,
        newestArchivedDay: null,
        reportsNotRecorded: 208729,
        reportsFromPushedSources: 0,
        reportsFromArchivableSources: 208729,
        reportsIngestedBeforeTheArchive: 208729,
        estimatedToStayUnrecorded: 208729,
      }),
    )
    expect(text).toBe('There is no archive to re-read, so all of them stay "not recorded". Reports ingested from now on get the fields when they arrive.')
    expect(text).not.toMatch(/archived day|pushed/)
  })

  it('gives the breakdown and the estimate when some can be filled', () => {
    expect(describeReportsWithoutFields(preview())).toBe(
      '20 reports from pushed sources were never archived; 100 reports were ingested before the oldest archived day. ' +
        'About 120 will stay "not recorded" whatever happens; the rest depends on what the archive holds.',
    )
  })

  it('leaves out a zero clause, and says all of them when none can be filled', () => {
    expect(
      describeReportsWithoutFields(
        preview({ reportsNotRecorded: 300, reportsFromPushedSources: 0, reportsIngestedBeforeTheArchive: 300, estimatedToStayUnrecorded: 300 }),
      ),
    ).toBe('300 reports were ingested before the oldest archived day. All of them will stay "not recorded".')
  })
})

describe('ReportBackfillCard', () => {
  // Braces matter: mockReset() returns the mock, and a function returned from a hook is
  // registered as a cleanup hook — vitest would then call fetchJson() after every test.
  beforeEach(() => {
    fetchJson.mockReset()
  })

  it('shows the archive, the rows without the fields, and what will stay unrecorded', async () => {
    answer(preview(), state())
    render(<ReportBackfillCard />)

    expect(await screen.findByText('Configured')).toBeInTheDocument()
    expect(screen.getByText(/1,200 archived mails under dmarc\/reports\//)).toBeInTheDocument()
    expect(screen.getByText('500')).toBeInTheDocument()
    expect(screen.getByText(/About 120 will stay "not recorded"/)).toBeInTheDocument()
    expect(screen.getByText('Not run')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Run backfill' })).toBeEnabled()
  })

  it('cannot run without an archive, and says why', async () => {
    answer(preview({ archiveConfigured: false, archive: null, archivedObjects: 0, oldestArchivedDay: null, newestArchivedDay: null }), state())
    render(<ReportBackfillCard />)

    expect(await screen.findByText('Not configured')).toBeInTheDocument()
    expect(screen.getByText(/No bucket is configured/)).toBeInTheDocument()
    expect(screen.getByText(/There is no archive to re-read/)).toBeInTheDocument()
    expect(screen.queryByText(/oldest archived day/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Run backfill' })).toBeDisabled()
  })

  it('queues a run and shows it as pending', async () => {
    answer(preview(), state())
    render(<ReportBackfillCard />)
    const run = await screen.findByRole('button', { name: 'Run backfill' })

    fireEvent.click(run)

    await waitFor(() => expect(screen.getByText('Queued for the worker')).toBeInTheDocument())
    expect(fetchJson).toHaveBeenCalledWith('/api/v1/admin/report-backfill/run', { method: 'POST' })
    expect(screen.getByRole('button', { name: 'Run backfill' })).toBeDisabled()
  })

  it('shows a failed run with its error and the counts it reached', async () => {
    answer(
      preview(),
      state({
        status: 'failed',
        pass: 'collect',
        lastError: 'second page unavailable',
        finishedAtUtc: '2026-10-13T12:00:00Z',
        objectsSeen: 10,
        payloadsParsed: 10,
        matched: 4,
      }),
    )
    render(<ReportBackfillCard />)

    expect(await screen.findByText('The last run failed')).toBeInTheDocument()
    expect(screen.getByText('second page unavailable')).toBeInTheDocument()
    expect(screen.getByText(/10 archived mails · 10 reports · 4 matched/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Run backfill' })).toBeEnabled()
  })
})
