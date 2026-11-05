import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { EnforcementRecordChange } from '@/lib/analytics'
import { EnforcementStepRecord } from '@/pages/DomainDetailPage'

const change: EnforcementRecordChange = {
  host: '_dmarc.example.co.uk',
  current: 'v=DMARC1; p=none; rua=mailto:dmarc@example.com',
  recommended: 'v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@example.com',
  changedTags: ['p', 't'],
  notes: ['t=y is the testing step: receivers see p=quarantine but apply one level below it.'],
}

/**
 * What gets pasted into a zone file is what this renders, so the assertions are on
 * the record string itself and on what the clipboard receives — not on the layout.
 */
describe('EnforcementStepRecord', () => {
  beforeEach(() => {
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } })
  })

  it('shows the host, the live record and the record to publish', () => {
    render(<EnforcementStepRecord change={change} unavailable={false} />)

    expect(screen.getByText('_dmarc.example.co.uk')).toBeInTheDocument()
    expect(screen.getByText('v=DMARC1; p=none; rua=mailto:dmarc@example.com')).toBeInTheDocument()
    expect(
      screen.getByText('v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@example.com'),
    ).toBeInTheDocument()
  })

  it('copies the recommended record, not the live one', async () => {
    render(<EnforcementStepRecord change={change} unavailable={false} />)

    fireEvent.click(screen.getByRole('button', { name: 'Copy the record to publish' }))

    await waitFor(() =>
      expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
        'v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@example.com',
      ),
    )
  })

  it('names the tags the step changes, so the rest reads as untouched', () => {
    render(<EnforcementStepRecord change={change} unavailable={false} />)

    expect(screen.getByText(/Changes p= and t=\. Everything else stays as published\./)).toBeInTheDocument()
  })

  it('renders the notes attached to the change', () => {
    render(<EnforcementStepRecord change={change} unavailable={false} />)

    expect(screen.getByText(/t=y is the testing step/)).toBeInTheDocument()
  })

  /** "If the live record could not be read, say so and show nothing." */
  it('says why there is nothing to copy when a step is due but no record was built', () => {
    render(<EnforcementStepRecord change={null} unavailable />)

    expect(
      screen.getByText(/No DMARC record of this domain's own could be read from DNS/),
    ).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('renders nothing at all when no step is due', () => {
    const { container } = render(<EnforcementStepRecord change={null} unavailable={false} />)

    expect(container).toBeEmptyDOMElement()
  })
})
