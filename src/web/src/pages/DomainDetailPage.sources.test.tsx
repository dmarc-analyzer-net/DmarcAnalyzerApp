import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { SourceIpCell } from '@/pages/DomainDetailPage'

/**
 * A reporter can send messages with no source IP. Those group into a source row whose IP is
 * empty, and everything the row's first cell offers — expanding it, fetching its detail
 * panel — is keyed by that IP, so it rendered a blank expander that answered
 * "ip query parameter is required" when clicked (#190).
 */
describe('SourceIpCell', () => {
  it('renders the IP as an expander', () => {
    render(<SourceIpCell ip="192.0.2.1" expanded={false} onToggle={vi.fn()} />)

    const expander = screen.getByRole('button')
    expect(expander).toHaveTextContent('192.0.2.1')
    expect(expander).toHaveAttribute('aria-expanded', 'false')
  })

  it('says the IP is missing, and offers nothing to expand, when the reporter sent none', () => {
    render(<SourceIpCell ip="" expanded={false} onToggle={vi.fn()} />)

    expect(screen.getByText('No source IP reported')).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('treats a whitespace-only IP as missing too', () => {
    render(<SourceIpCell ip="   " expanded={false} onToggle={vi.fn()} />)

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
