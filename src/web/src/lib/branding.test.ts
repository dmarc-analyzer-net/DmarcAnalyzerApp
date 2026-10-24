import { describe, expect, it } from 'vitest'

import { applyBranding, contrast, DEFAULT_BRANDING, deriveBrandPalette, parseBranding } from '@/lib/branding'

describe('deriveBrandPalette', () => {
  // A dark accent, the default, a mid tone, and three that fail white text.
  const accents = ['#0c7568', '#1d4ed8', '#e11d48', '#f5c400', '#ffffff', '#7dd3fc']

  it.each(accents)('keeps every text pairing at AA for %s', (accent) => {
    const p = deriveBrandPalette(accent)

    expect(contrast(p['--text-on-brand'], p['--brand'])).toBeGreaterThanOrEqual(4.5)
    expect(contrast(p['--link'], '#ffffff')).toBeGreaterThanOrEqual(4.5)
    expect(contrast(p['--brand-strong'], p['--brand-subtle'])).toBeGreaterThanOrEqual(4.5)
    expect(contrast(p['--brand-chart'], '#ffffff')).toBeGreaterThanOrEqual(3)
  })

  it('keeps the accent itself on the button', () => {
    expect(deriveBrandPalette('#f5c400')['--brand']).toBe('#f5c400')
  })

  it('puts white text on a dark accent and dark text on a light one', () => {
    expect(deriveBrandPalette('#0c7568')['--text-on-brand']).toBe('#ffffff')
    expect(deriveBrandPalette('#f5c400')['--text-on-brand']).toBe('#0b1d18')
  })

  it('darkens on hover rather than lightening', () => {
    for (const accent of accents) {
      const p = deriveBrandPalette(accent)
      expect(contrast(p['--brand-hover'], '#ffffff')).toBeGreaterThan(contrast(p['--brand'], '#ffffff'))
    }
  })
})

describe('parseBranding', () => {
  const valid = {
    name: 'Example Agency',
    logoUrl: 'https://example.com/logo.png',
    accentColor: '#F5C400',
    customized: true,
    showPoweredBy: true,
  }

  it('accepts a server payload and lowercases the accent', () => {
    expect(parseBranding(valid)).toEqual({ ...valid, accentColor: '#f5c400' })
  })

  it.each([
    ['a script URL', { ...valid, logoUrl: 'javascript:alert(1)' }],
    ['a CSS injection', { ...valid, accentColor: '#000000;background:url(x)' }],
    ['a blank name', { ...valid, name: '  ' }],
    ['not an object', 'Example Agency'],
  ])('rejects %s', (_, value) => {
    expect(parseBranding(value)).toBeNull()
  })
})

describe('applyBranding', () => {
  it('sets the palette when customised and clears it again when not', () => {
    const root = document.createElement('div')

    applyBranding({ ...DEFAULT_BRANDING, accentColor: '#f5c400', customized: true }, root)
    expect(root.style.getPropertyValue('--brand')).toBe('#f5c400')
    expect(root.style.getPropertyValue('--text-on-brand')).toBe('#0b1d18')

    applyBranding(DEFAULT_BRANDING, root)
    expect(root.style.getPropertyValue('--brand')).toBe('')
  })
})
