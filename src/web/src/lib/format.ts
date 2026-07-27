const trimTrailingZero = (value: string) => (value.endsWith('.0') ? value.slice(0, -2) : value)

/** Compact volume formatting: 987 -> "987", 12_345 -> "12.3k", 1_234_567 -> "1.2M". */
export function formatCompact(value: number): string {
  if (!Number.isFinite(value)) return '—'
  const abs = Math.abs(value)
  if (abs >= 1_000_000_000) return `${trimTrailingZero((value / 1_000_000_000).toFixed(1))}B`
  if (abs >= 1_000_000) return `${trimTrailingZero((value / 1_000_000).toFixed(1))}M`
  if (abs >= 1_000) return `${trimTrailingZero((value / 1_000).toFixed(1))}k`
  return value.toLocaleString('en-US')
}

/** Formats an already-scaled 0..100 percent as "89.1%". Never rounds up to
 * "100.0%" while any mail still fails, so a domain at 99.96% doesn't read as
 * fully compliant next to a "below target" label.
 *
 * Pass the *unrounded* value: pre-rounding with `toFixed(1)` destroys the
 * precision this guard depends on, which is how the Domains list and the
 * dashboard ended up disagreeing about the same domain. */
export function formatPercentValue(percent: number | null | undefined): string {
  if (percent == null || !Number.isFinite(percent)) return '—'
  return `${(percent < 100 ? Math.min(percent, 99.9) : percent).toFixed(1)}%`
}

/** Formats a 0..1 fraction as "89.1%". See {@link formatPercentValue}. */
export function formatPercent(fraction: number | null | undefined): string {
  if (fraction == null || !Number.isFinite(fraction)) return '—'
  return formatPercentValue(fraction * 100)
}

/** Short UTC date without year, e.g. "Aug 20". Accepts "yyyy-MM-dd" or ISO timestamps. */
export function formatShortDate(iso: string): string {
  const date = new Date(iso.length === 10 ? `${iso}T00:00:00Z` : iso)
  if (Number.isNaN(date.getTime())) return iso
  return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' })
}

/** Full UTC date, e.g. "Aug 20, 2024". */
export function formatFullDate(iso: string): string {
  const date = new Date(iso.length === 10 ? `${iso}T00:00:00Z` : iso)
  if (Number.isNaN(date.getTime())) return iso
  return date.toLocaleDateString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    timeZone: 'UTC',
  })
}

/**
 * Exact UTC instant, e.g. "2026-07-27 08:00 UTC"; "—" for null.
 *
 * Deliberately not localised and deliberately not relative: this is for values an
 * operator compares against another record — two artifacts a day apart, an offload
 * timestamp against a log line — where "3d ago" in the reader's own timezone is the
 * wrong answer twice over.
 */
export function formatUtcDateTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return `${date.toISOString().slice(0, 16).replace('T', ' ')} UTC`
}

/** Relative for recent dates ("Today", "3d ago"), short full date otherwise; "Never" for null. */
export function formatRelativeOrDate(iso: string | null): string {
  if (!iso) return 'Never'
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  const diffMs = Date.now() - date.getTime()
  const dayMs = 24 * 60 * 60 * 1000
  if (diffMs >= 0 && diffMs < dayMs) return 'Today'
  if (diffMs >= dayMs && diffMs < 2 * dayMs) return 'Yesterday'
  if (diffMs >= 0 && diffMs < 7 * dayMs) return `${Math.floor(diffMs / dayMs)}d ago`
  return formatFullDate(iso)
}
