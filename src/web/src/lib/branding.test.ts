import { describe, expect, it } from 'vitest'

import { applyBranding, contrast, DEFAULT_BRANDING, deriveBrandPalette, parseBranding } from '@/lib/branding'

// Card, page, sunken rows and the four status tints — every light surface a
// brand-coloured link or focus ring can sit on.
const SURFACES = ['#ffffff', '#f5f8f7', '#eef3f1', '#d7f4ec', '#fdf0d5', '#fde5ea']

describe('deriveBrandPalette', () => {
  // The default, dark and mid tones, three that fail white text, and two greys
  // where neither white nor dark text clears AA or the pressed state has no room.
  const accents = ['#0c7568', '#1d4ed8', '#e11d48', '#f5c400', '#ffffff', '#7dd3fc', '#808080', '#999999']

  it.each(accents)('keeps every text pairing at AA for %s', (accent) => {
    const p = deriveBrandPalette(accent)

    for (const fill of [p['--brand'], p['--brand-hover'], p['--brand-active']]) {
      expect(contrast(p['--text-on-brand'], fill)).toBeGreaterThanOrEqual(4.5)
    }
    expect(contrast(p['--brand-strong'], p['--brand-subtle'])).toBeGreaterThanOrEqual(4.5)
    for (const surface of SURFACES) {
      expect(contrast(p['--link'], surface)).toBeGreaterThanOrEqual(4.5)
      expect(contrast(p['--brand-ring'], surface)).toBeGreaterThanOrEqual(3)
    }
  })

  it("keeps index.css's default ring, teal-600, at 3:1 on every surface", () => {
    for (const surface of SURFACES) {
      expect(contrast('#0e9481', surface)).toBeGreaterThanOrEqual(3)
    }
  })

  it('draws a solid focus ring, so a white accent still shows one', () => {
    expect(deriveBrandPalette('#ffffff')['--brand-ring']).toMatch(/^#[0-9a-f]{6}$/)
  })

  it('darkens a mid grey until white text clears AA, rather than failing with either', () => {
    const p = deriveBrandPalette('#808080')
    expect(p['--text-on-brand']).toBe('#ffffff')
    expect(p['--brand']).not.toBe('#808080')
  })

  it('keeps the accent itself on the button', () => {
    expect(deriveBrandPalette('#f5c400')['--brand']).toBe('#f5c400')
  })

  it('puts white text on a dark accent and dark text on a light one', () => {
    expect(deriveBrandPalette('#0c7568')['--text-on-brand']).toBe('#ffffff')
    expect(deriveBrandPalette('#f5c400')['--text-on-brand']).toBe('#0b1d18')
  })

  it('darkens on hover rather than lightening whenever there is room to', () => {
    for (const accent of accents.filter((a) => a !== '#999999')) {
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
