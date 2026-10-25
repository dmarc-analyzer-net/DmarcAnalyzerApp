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

/** The only accent form accepted: six hex digits, as the server's own check. */
const HEX = /^#[0-9a-f]{6}$/i
/** localStorage key for the last brand seen. */
const CACHE_KEY = 'dmarc-analyzer.branding'

/** True for an absolute http(s) URL — the only kind allowed into an `<img src>`. */
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

/**
 * The brand from the last visit, applied before the first render so a returning
 * visitor never sees the product's brand flash. Null when there is none, or when
 * storage is unavailable or holds something that no longer validates.
 */
export function readCachedBranding(): Branding | null {
  try {
    const raw = localStorage.getItem(CACHE_KEY)
    return raw ? parseBranding(JSON.parse(raw)) : null
  } catch {
    return null
  }
}

/** Remembers the brand for the next visit's first paint. Never throws. */
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
/**
 * Every light surface a brand-coloured link or focus ring can sit on: card, page,
 * sunken rows, and the four status tints (a link inside a warning callout).
 */
const SURFACES = ['#ffffff', '#f5f8f7', '#eef3f1', '#d7f4ec', '#fdf0d5', '#fde5ea']

/** `#rrggbb` to its three 0–255 channels. Expects a value that already passed {@link HEX}. */
function toRgb(hex: string): Rgb {
  const n = Number.parseInt(hex.slice(1), 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

/** Three 0–255 channels, rounded, back to lowercase `#rrggbb`. */
function toHex(rgb: Rgb): string {
  return `#${rgb.map((c) => Math.round(c).toString(16).padStart(2, '0')).join('')}`
}

/** `weight` of `b` mixed into `a`, in sRGB — what CSS color-mix(in srgb) does. */
function mix(a: Rgb, b: Rgb, weight: number): Rgb {
  return [0, 1, 2].map((i) => a[i] + (b[i] - a[i]) * weight) as Rgb
}

/** WCAG 2 relative luminance, 0 for black to 1 for white — the input to {@link contrast}. */
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

/**
 * Darkens `base` towards black, from `start`, until it reaches `ratio` against
 * `against` — or against every one of them, given a list.
 */
function darkenUntil(base: Rgb, against: string | string[], ratio: number, start = 0): string {
  const backgrounds = typeof against === 'string' ? [against] : against
  for (let w = start; w < 1; w += 0.05) {
    const candidate = toHex(mix(base, BLACK, w))
    if (backgrounds.every((bg) => contrast(candidate, bg) >= ratio)) return candidate
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
  '--text-on-brand': string
  '--link': string
  '--link-hover': string
}

/**
 * Everything the console colours with the brand, from one six-digit accent.
 *
 * The accent itself is used as given on primary buttons, and the text on them is
 * white when that clears WCAG AA (4.5:1) and dark when it does not — so a yellow
 * accent keeps its yellow and gets legible text. A mid-tone where neither clears
 * it (a #808080 grey) is darkened until white does. Hover and pressed shades stay
 * at 4.5:1 against that same text.
 *
 * Everything that is text on a light surface (links, the active nav item, badges)
 * is darkened from the accent until it clears 4.5:1 on every surface it can sit
 * on. The focus ring is drawn solid and darkened until it clears the 3:1 that
 * WCAG asks of a focus indicator, so even a white accent shows one. Data colours
 * (chart bars, status badges) are not brand colours and are not derived here.
 */
export function deriveBrandPalette(accent: string): BrandPalette {
  const accentRgb = toRgb(accent)
  const [brand, onBrand] =
    contrast(accent, '#ffffff') >= 4.5
      ? [accent, '#ffffff']
      : contrast(accent, INK) >= 4.5
        ? [accent, INK]
        : [darkenUntil(accentRgb, '#ffffff', 4.5), '#ffffff']
  const base = toRgb(brand)
  const subtle = toHex(mix(accentRgb, WHITE, 0.93))
  const link = darkenUntil(accentRgb, SURFACES, 4.5)

  return {
    '--brand': brand,
    '--brand-hover': pressShade(base, onBrand, 0.2),
    '--brand-active': pressShade(base, onBrand, 0.37),
    '--brand-subtle': subtle,
    '--brand-ring': darkenUntil(accentRgb, SURFACES, 3),
    '--brand-strong': darkenUntil(accentRgb, subtle, 4.5, 0.2),
    '--text-on-brand': onBrand,
    '--link': link,
    '--link-hover': toHex(mix(toRgb(link), BLACK, 0.2)),
  }
}

/**
 * A hover or pressed shade of `base` that keeps 4.5:1 against `text`. It darkens
 * by up to `weight`, as every other button does; under dark text there may be no
 * room to darken at all, and then it lightens by the same step instead, which only
 * adds contrast.
 */
function pressShade(base: Rgb, text: string, weight: number): string {
  for (let w = weight; w > 0.001; w -= 0.05) {
    const candidate = toHex(mix(base, BLACK, w))
    if (contrast(candidate, text) >= 4.5) return candidate
  }
  return toHex(mix(base, WHITE, weight))
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
