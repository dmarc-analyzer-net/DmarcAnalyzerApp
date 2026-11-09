import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { DomainAnalytics } from '@/lib/analytics'

// Mocked because this test is about what the posture panel renders and what clicking
// it does to the table, not about the network or the session.
vi.mock('@/lib/api', () => ({
  fetchJson: vi.fn(),
  ApiError: class extends Error {},
}))
vi.mock('@/lib/auth-context', () => ({
  useAuth: () => ({
    status: 'authenticated',
    user: { id: 'u1', email: 'admin@agency.tld', displayName: 'Admin', role: 'agency_admin' },
    login: vi.fn(),
    logout: vi.fn(),
  }),
}))

import { fetchJson } from '@/lib/api'
import { DomainsPage } from '@/pages/DomainsPage'

function domain(
  overrides: Partial<DomainAnalytics> & Pick<DomainAnalytics, 'name'>,
): DomainAnalytics {
  return {
    domainId: `id-${overrides.name}`,
    isActive: true,
    clientId: 'c1',
    clientName: 'Acme',
    clientSlug: 'acme',
    messages: 1000,
    compliantMessages: 1000,
    complianceRate: 1,
    dkimPassRate: 1,
    spfPassRate: 1,
    reports: 10,
    sources: 5,
    reporters: 3,
    quarantined: 0,
    rejected: 0,
    lastReportEndUtc: '2026-07-20T00:00:00Z',
    status: 'aligned',
    publishedPolicy: 'reject',
    subdomainPolicy: null,
    publishedPct: 100,
    dkimAlignment: 'relaxed',
    spfAlignment: 'relaxed',
    dnsLookupStatus: 'found',
    dnsPolicyInheritedFrom: null,
    dnsCheckedAtUtc: '2026-07-27T00:00:00Z',
    enforcementStatus: 'enforced',
    ...overrides,
  }
}

/** Four postures, deliberately including one silent domain and one unprotected one. */
const analytics: DomainAnalytics[] = [
  domain({ name: 'enforced.test' }),
  domain({ name: 'ramping.test', publishedPolicy: 'quarantine', enforcementStatus: 'ramping' }),
  domain({
    name: 'spoofed.test',
    publishedPolicy: 'none',
    enforcementStatus: 'spoofing',
    messages: 500,
    compliantMessages: 100,
    complianceRate: 0.2,
    dkimPassRate: 0.15,
    spfPassRate: 0.05,
    status: 'critical',
  }),
  domain({
    name: 'silent.test',
    enforcementStatus: 'no_data',
    publishedPolicy: null,
    dnsLookupStatus: 'missing',
    messages: 0,
    compliantMessages: 0,
    complianceRate: 0,
    dkimPassRate: 0,
    spfPassRate: 0,
    reports: 0,
    sources: 0,
    reporters: 0,
    status: 'no_data',
  }),
]

const mockedFetch = vi.mocked(fetchJson)

function stubEndpoints() {
  mockedFetch.mockImplementation(async (url: string) => {
    if (url.startsWith('/api/v1/clients')) {
      return [{ id: 'c1', name: 'Acme', slug: 'acme' }] as never
    }
    if (url.startsWith('/api/v1/domains')) return [] as never
    if (url.startsWith('/api/v1/analytics/domains')) return analytics as never
    throw new Error(`unexpected url ${url}`)
  })
}

async function renderPage() {
  render(
    <MemoryRouter initialEntries={['/domains']}>
      <DomainsPage />
    </MemoryRouter>,
  )
  await waitFor(() => expect(screen.getByText('enforced.test')).toBeInTheDocument())
}

/** The chip for one posture, addressed by its label rather than by DOM position. */
function chip(label: string) {
  return screen.getByRole('button', { name: new RegExp(`^${label}`) })
}

function tableRowNames() {
  return screen
    .getAllByRole('row')
    .map((row) => row.textContent ?? '')
    .filter((text) => text.includes('.test'))
}

describe('DomainsPage posture panel', () => {
  beforeEach(() => {
    mockedFetch.mockReset()
    stubEndpoints()
  })

  it('counts the domains in scope by posture', async () => {
    await renderPage()

    expect(screen.getByText('Enforcement posture')).toBeInTheDocument()
    // enforced + ramping are the two postures that publish an enforcing policy.
    expect(screen.getByText('2 of 4 domains publish an enforcing policy')).toBeInTheDocument()
    expect(within(chip('Enforced')).getByText('1')).toBeInTheDocument()
    expect(within(chip('Spoofing')).getByText('1')).toBeInTheDocument()
    expect(within(chip('Monitoring')).getByText('0')).toBeInTheDocument()
  })

  it('filters the table to one posture and keeps the portfolio bar intact', async () => {
    await renderPage()

    fireEvent.click(chip('Spoofing'))

    await waitFor(() => expect(screen.queryByText('enforced.test')).not.toBeInTheDocument())
    expect(screen.getByText('spoofed.test')).toBeInTheDocument()
    expect(chip('Spoofing')).toHaveAttribute('aria-pressed', 'true')
    // The counts still describe the whole scope — selecting a posture must not
    // collapse the bar to the one segment just chosen.
    expect(within(chip('Enforced')).getByText('1')).toBeInTheDocument()
    expect(screen.getByText('2 of 4 domains publish an enforcing policy')).toBeInTheDocument()
  })

  it('clears the filter when the active chip is clicked again', async () => {
    await renderPage()

    fireEvent.click(chip('Spoofing'))
    await waitFor(() => expect(screen.queryByText('enforced.test')).not.toBeInTheDocument())

    fireEvent.click(chip('Spoofing'))

    await waitFor(() => expect(screen.getByText('enforced.test')).toBeInTheDocument())
    expect(chip('Spoofing')).toHaveAttribute('aria-pressed', 'false')
    expect(tableRowNames()).toHaveLength(4)
  })

  it('leaves a posture with no domains unclickable rather than hiding it', async () => {
    await renderPage()

    expect(chip('Monitoring')).toBeDisabled()
  })
})

describe('DomainsPage row visuals', () => {
  beforeEach(() => {
    mockedFetch.mockReset()
    stubEndpoints()
  })

  it('shows which mechanism carries alignment on every reporting row', async () => {
    await renderPage()

    // One DKIM and one SPF meter per reporting row; the silent row has neither.
    expect(screen.getAllByText('DKIM')).toHaveLength(3)
    expect(screen.getAllByText('SPF')).toHaveLength(3)
  })

  it('describes the aligned share of a row as a proportion, not just a percentage', async () => {
    await renderPage()

    expect(
      screen.getByRole('img', { name: '100 of 500 messages aligned' }),
    ).toBeInTheDocument()
  })

  it('leaves a silent domain without bars, so no data cannot read as total failure', async () => {
    await renderPage()

    const silentRow = screen.getAllByRole('row').find((row) => row.textContent?.includes('silent.test'))
    expect(silentRow).toBeDefined()
    expect(within(silentRow!).queryByRole('img')).not.toBeInTheDocument()
    expect(within(silentRow!).getAllByText('—').length).toBeGreaterThan(0)
  })
})
