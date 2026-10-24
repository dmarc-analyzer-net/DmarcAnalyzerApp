import { createContext, useContext } from 'react'

/** GET /api/v1/branding — the instance brand, already validated by the server. */
export type Branding = {
  name: string
  logoUrl: string | null
  accentColor: string
  /** False on an instance still wearing the product's own brand. */
  customized: boolean
  showPoweredBy: boolean
}

export const PRODUCT_NAME = 'DMARC Analyzer'

export const DEFAULT_BRANDING: Branding = {
  name: PRODUCT_NAME,
  logoUrl: null,
  accentColor: '#0c7568',
  customized: false,
  showPoweredBy: false,
}

/** null while the first fetch is in flight and nothing is cached: render no brand yet. */
export const BrandingContext = createContext<Branding | null>(DEFAULT_BRANDING)

/**
 * The brand to render, or null while it is still unknown. Outside a provider
 * (component tests) it is the product's own brand.
 */
export function useBranding(): Branding | null {
  return useContext(BrandingContext)
}

// ---- Validation and cache ------------------------------------------------------

const HEX = /^#[0-9a-f]{6}$/i
const CACHE_KEY = 'dmarc-analyzer.branding'

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value)
    return url.protocol === 'https:' || url.protocol === 'http:'
  } catch {
    return false
  }
}

/**
 * Accepts a payload from the server or the cache, or returns null. The server has
 * already validated it; this repeats the two checks that guard what reaches an
 * `src` and a CSS variable, because the cache is just a string anyone on the
 * origin can write.
 */
export function parseBranding(value: unknown): Branding | null {
  if (typeof value !== 'object' || value === null) return null
  const v = value as Record<string, unknown>
  if (typeof v.name !== 'string' || v.name.trim() === '') return null
  if (typeof v.accentColor !== 'string' || !HEX.test(v.accentColor)) return null
  if (v.logoUrl !== null && (typeof v.logoUrl !== 'string' || !isHttpUrl(v.logoUrl))) return null
  return {
    name: v.name,
    logoUrl: v.logoUrl,
    accentColor: v.accentColor.toLowerCase(),
    customized: v.customized === true,
    showPoweredBy: v.showPoweredBy === true,
  }
}

export function readCachedBranding(): Branding | null {
  try {
    const raw = localStorage.getItem(CACHE_KEY)
    return raw ? parseBranding(JSON.parse(raw)) : null
  } catch {
    return null
  }
}

export function writeCachedBranding(branding: Branding): void {
  try {
    localStorage.setItem(CACHE_KEY, JSON.stringify(branding))
  } catch {
    // Storage full or disabled: the next visit fetches before painting the brand.
  }
}

// ---- Colour --------------------------------------------------------------------

type Rgb = [number, number, number]

const WHITE: Rgb = [255, 255, 255]
const BLACK: Rgb = [0, 0, 0]
/** --surface-ink, the dark text used on a light accent. */
const INK = '#0b1d18'

function toRgb(hex: string): Rgb {
  const n = Number.parseInt(hex.slice(1), 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

function toHex(rgb: Rgb): string {
  return `#${rgb.map((c) => Math.round(c).toString(16).padStart(2, '0')).join('')}`
}

/** `weight` of `b` mixed into `a`, in sRGB — what CSS color-mix(in srgb) does. */
function mix(a: Rgb, b: Rgb, weight: number): Rgb {
  return [0, 1, 2].map((i) => a[i] + (b[i] - a[i]) * weight) as Rgb
}

function luminance([r, g, b]: Rgb): number {
  const channel = (c: number) => {
    const s = c / 255
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)
}

/** WCAG 2 contrast ratio, 1 to 21. */
export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(toRgb(a)), luminance(toRgb(b))].sort((x, y) => y - x)
  return (hi + 0.05) / (lo + 0.05)
}

/** Darkens `base` towards black, from `start`, until it reaches `ratio` against `against`. */
function darkenUntil(base: Rgb, against: string, ratio: number, start = 0): string {
  for (let w = start; w < 1; w += 0.05) {
    const candidate = toHex(mix(base, BLACK, w))
    if (contrast(candidate, against) >= ratio) return candidate
  }
  return toHex(BLACK)
}

/** The CSS variables index.css defines for the brand, derived from one accent. */
export type BrandPalette = {
  '--brand': string
  '--brand-hover': string
  '--brand-active': string
  '--brand-subtle': string
  '--brand-ring': string
  '--brand-strong': string
  '--brand-chart': string
  '--text-on-brand': string
  '--link': string
  '--link-hover': string
}

/**
 * Everything the console colours with the brand, from one six-digit accent.
 *
 * The accent itself is used as given on primary buttons, and the text on them is
 * white when that clears WCAG AA (4.5:1) and dark when it does not — so a yellow
 * accent keeps its yellow and gets legible text. Everything that is text on a
 * light surface (links, the active nav item, badges) is darkened from the accent
 * until it clears 4.5:1, and chart bars until they clear the 3:1 graphics bar.
 */
export function deriveBrandPalette(accent: string): BrandPalette {
  const base = toRgb(accent)
  const whiteText = contrast(accent, '#ffffff') >= 4.5
  const onBrand = whiteText
    ? '#ffffff'
    : contrast(accent, INK) >= contrast(accent, '#ffffff')
      ? INK
      : '#ffffff'
  // Hover always darkens. Under dark text it darkens less, so the pressed state
  // does not drag the button into its own text colour.
  const [hover, active] = onBrand === INK ? [0.1, 0.2] : [0.2, 0.37]
  const subtle = toHex(mix(base, WHITE, 0.93))
  const link = darkenUntil(base, '#ffffff', 4.5)

  return {
    '--brand': accent,
    '--brand-hover': toHex(mix(base, BLACK, hover)),
    '--brand-active': toHex(mix(base, BLACK, active)),
    '--brand-subtle': subtle,
    '--brand-ring': `rgba(${base.join(', ')}, 0.28)`,
    '--brand-strong': darkenUntil(base, subtle, 4.5, 0.2),
    '--brand-chart': darkenUntil(base, '#ffffff', 3),
    '--text-on-brand': onBrand,
    '--link': link,
    '--link-hover': toHex(mix(toRgb(link), BLACK, 0.2)),
  }
}

/**
 * Puts the brand on the document: the palette on :root, and nothing at all for
 * an uncustomised instance, so index.css's hand-tuned defaults stand untouched.
 */
export function applyBranding(branding: Branding, root: HTMLElement = document.documentElement): void {
  const palette = deriveBrandPalette(branding.accentColor)
  for (const [name, value] of Object.entries(palette)) {
    if (branding.customized) {
      root.style.setProperty(name, value)
    } else {
      root.style.removeProperty(name)
    }
  }
}
