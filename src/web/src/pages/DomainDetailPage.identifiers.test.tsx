import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { ValueList } from '@/pages/DomainDetailPage'

/**
 * A reporter can report an empty envelope sender, and sends it as the RFC 5321 null
 * reverse-path `<>`. That went to the panel verbatim, where it read as a stray glyph
 * (#196) — but it is real data, distinct from the reporter omitting the element, so it
 * is labelled rather than blanked.
 */
describe('ValueList', () => {
  it('renders an ordinary identifier verbatim', () => {
    render(<ValueList items={[{ value: 'mail.acme.example', messages: 12 }]} emptyText="none" />)

    expect(screen.getByText('mail.acme.example')).toBeInTheDocument()
  })

  it('names the null reverse-path instead of showing the bare angle brackets', () => {
    render(<ValueList items={[{ value: '<>', messages: 12 }]} emptyText="none" nullSender />)

    const entry = screen.getByTitle(/null reverse-path/i)
    expect(entry).toHaveTextContent('null sender')
    expect(entry).toHaveTextContent('<>')
  })

  it('recognises the null reverse-path from a reporter that pretty-prints its XML', () => {
    render(<ValueList items={[{ value: '\n      <>\n    ', messages: 12 }]} emptyText="none" nullSender />)

    expect(screen.getByTitle(/null reverse-path/i)).toHaveTextContent('null sender')
  })

  it('does not call it a null sender when the value merely contains angle brackets', () => {
    render(<ValueList items={[{ value: '<script>', messages: 12 }]} emptyText="none" nullSender />)

    expect(screen.queryByTitle(/null reverse-path/i)).not.toBeInTheDocument()
    expect(screen.getByText('<script>')).toBeInTheDocument()
  })

  /**
   * The header-from list renders through the same component, and `<>` there is a malformed
   * `From:`, not a null envelope sender. Labelling it would assert the source sent bounces
   * on the evidence of a broken reporter, so the label is opt-in per list.
   */
  it('leaves the angle brackets alone in a list that is not the envelope sender', () => {
    render(<ValueList items={[{ value: '<>', messages: 12 }]} emptyText="none" />)

    expect(screen.queryByTitle(/null reverse-path/i)).not.toBeInTheDocument()
    expect(screen.getByText('<>')).toBeInTheDocument()
  })

  it('falls back to the empty text when the reporter sent nothing to list', () => {
    render(<ValueList items={[]} emptyText="No envelope-from domains reported." />)

    expect(screen.getByText('No envelope-from domains reported.')).toBeInTheDocument()
  })
})
