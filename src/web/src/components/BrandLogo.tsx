import { useId } from 'react'

import { cn } from '@/lib/utils'

type BrandLogoProps = {
  /** 'full' is the mark plus wordmark; 'mark' is the badge on its own. */
  variant?: 'full' | 'mark'
  /** Rendered size of the mark in px. The wordmark scales from it. */
  height?: number
  /** For dark ink panels — lifts the wordmark to the light mint tint. */
  dark?: boolean
  className?: string
}

/**
 * Brand lockup from the DMARC Analyzer design system: a gradient teal shield
 * mark beside a Space Grotesk wordmark. The distributable copies of the same
 * artwork live in `public/logo.svg` and `public/favicon.svg`.
 *
 * The gradient stops are brand artwork values rather than design tokens, so they
 * stay literal here — that is what keeps this identical to those two files.
 */
export function BrandLogo({
  variant = 'full',
  height = 30,
  dark = false,
  className,
}: BrandLogoProps) {
  // Gradient ids must be unique per instance, or a second mark on the page
  // reuses the first one's def and the document carries duplicate ids.
  const gradientId = `brand-mark-${useId().replace(/:/g, '')}`

  const mark = (
    <svg
      viewBox="0 0 70 70"
      width={height}
      height={height}
      xmlns="http://www.w3.org/2000/svg"
      className={variant === 'mark' ? className : 'block shrink-0'}
      // In the full lockup the wordmark carries the accessible name, so the mark
      // is decorative; on its own it has to name itself.
      {...(variant === 'mark'
        ? { role: 'img', 'aria-label': 'DMARC Analyzer' }
        : { 'aria-hidden': true })}
    >
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="70" y2="70" gradientUnits="userSpaceOnUse">
          <stop offset="0" stopColor="#0d9488" />
          <stop offset="1" stopColor="#115e59" />
        </linearGradient>
      </defs>
      <rect width="70" height="70" rx="16" fill={`url(#${gradientId})`} />
      <g
        transform="translate(14 14) scale(1.75)"
        fill="none"
        stroke="#f0fdfa"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z" />
        <path d="M13 8.5 10.5 12.5h3L11 16.5" />
      </g>
    </svg>
  )

  if (variant === 'mark') {
    return mark
  }

  return (
    <span
      className={cn('inline-flex items-center', className)}
      style={{ gap: Math.round(height * 0.33) }}
    >
      {mark}
      <span
        className={cn(
          'whitespace-nowrap font-display font-semibold leading-none tracking-tight',
          dark ? 'text-[#f0fdfa]' : 'text-body',
        )}
        style={{ fontSize: Math.round(height * 0.55) }}
      >
        DMARC Analyzer
      </span>
    </span>
  )
}
