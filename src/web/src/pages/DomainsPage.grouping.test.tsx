import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { DomainAnalytics } from '@/lib/analytics'

// Mocked because this test is about what the table renders, not about the network or the
// session. Everything else — the grouping, the sort, the policy cell — is the real thing.
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

/** A domain row with enough volume that it is not a no_data row. */
function domain(overrides: Partial<DomainAnalytics> & Pick<DomainAnalytics, 'name'>): DomainAnalytics {
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

/**
 * The shape found on the real instance: a monitored parent with subdomains, a pair of
 * siblings whose parent is *not* monitored, and a lone subdomain that must stay flat.
 */
const analytics: DomainAnalytics[] = [
  domain({ name: 'yulsn.io' }),
  domain({
    name: 'client.yulsn.io',
    dnsLookupStatus: 'inherited',
    dnsPolicyInheritedFrom: 'yulsn.io',
  }),
  // Publishes its own weaker record, so it must not be shown as the parent's reject.
  domain({
    name: 'gitlab.yulsn.io',
    publishedPolicy: 'none',
    enforcementStatus: 'monitoring',
  }),
  domain({ name: 'booking.smalldanishhotels.dk' }),
  domain({ name: 'nyheder.smalldanishhotels.dk' }),
  domain({ name: 'email.krifa.dk' }),
]

function renderPage() {
  vi.mocked(fetchJson).mockImplementation(async (url: string) => {
    if (url.startsWith('/api/v1/clients')) return [] as never
    if (url.startsWith('/api/v1/domains')) return [] as never
    if (url.startsWith('/api/v1/analytics/domains')) return analytics as never
    throw new Error(`unexpected request: ${url}`)
  })

  return render(
    <MemoryRouter>
      <DomainsPage />
    </MemoryRouter>,
  )
}

describe('Domains list grouping', () => {
  beforeEach(() => vi.clearAllMocks())

  it('renders every domain exactly once', async () => {
    renderPage()

    await waitFor(() => expect(screen.getByText('email.krifa.dk')).toBeInTheDocument())

    for (const row of analytics) {
      expect(screen.getAllByText(row.name)).toHaveLength(1)
    }
  })

  /**
   * The case that prompted all of this: two monitored siblings whose organisational domain
   * was never added, because reports only arrive for the sending subdomain.
   */
  it('renders a heading for a parent that is not monitored', async () => {
    renderPage()

    await waitFor(() => expect(screen.getByText('smalldanishhotels.dk')).toBeInTheDocument())

    // A label, not a row: it says so, and it is not a link to a domain page.
    expect(screen.getByText(/not monitored/)).toBeInTheDocument()
    expect(screen.getByText(/2 subdomains/)).toBeInTheDocument()
  })

  it('uses a monitored parent as its own heading rather than listing it twice', async () => {
    renderPage()

    await waitFor(() => expect(screen.getByText('yulsn.io')).toBeInTheDocument())

    // Present once, and as a real row — it keeps its policy badge, unlike the label above.
    expect(screen.getAllByText('yulsn.io')).toHaveLength(1)
    const row = screen.getByText('yulsn.io').closest('tr')
    expect(row).not.toBeNull()
    expect(within(row!).getByText('p=reject')).toBeInTheDocument()
  })

  it('marks an inherited policy with the domain it came from', async () => {
    renderPage()

    await waitFor(() => expect(screen.getByText('client.yulsn.io')).toBeInTheDocument())

    const row = screen.getByText('client.yulsn.io').closest('tr')!
    expect(within(row).getByText('p=reject')).toBeInTheDocument()
    expect(within(row).getByText('via yulsn.io')).toBeInTheDocument()
  })

  it('does not mark a domain that publishes its own record', async () => {
    renderPage()

    await waitFor(() => expect(screen.getByText('gitlab.yulsn.io')).toBeInTheDocument())

    // Opted out of the parent's reject: shown as the p=none it publishes, with no "via".
    const row = screen.getByText('gitlab.yulsn.io').closest('tr')!
    expect(within(row).getByText('p=none')).toBeInTheDocument()
    expect(within(row).queryByText(/^via /)).toBeNull()
  })

  it('leaves a lone subdomain flat, with no heading of its own', async () => {
    renderPage()

    await waitFor(() => expect(screen.getByText('email.krifa.dk')).toBeInTheDocument())

    // Grouping every subdomain-shaped row would have added ~36 single-child headings.
    expect(screen.queryByText('krifa.dk')).toBeNull()
  })
})
