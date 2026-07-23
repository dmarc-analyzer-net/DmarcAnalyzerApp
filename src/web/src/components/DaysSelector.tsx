import { ANALYTICS_DAYS_OPTIONS, type AnalyticsDays } from '@/lib/analytics'
import { cn } from '@/lib/utils'

type DaysSelectorProps = {
  value: AnalyticsDays
  onChange: (days: AnalyticsDays) => void
  disabled?: boolean
}

/** Segmented time-window selector (7 / 30 / 90 days) shared by analytics pages. */
export function DaysSelector({ value, onChange, disabled }: DaysSelectorProps) {
  return (
    <span
      role="group"
      aria-label="Time window"
      className="inline-flex shrink-0 gap-0.5 rounded-md bg-gray-100 p-[3px]"
    >
      {ANALYTICS_DAYS_OPTIONS.map((days) => {
        const active = value === days
        return (
          <button
            key={days}
            type="button"
            disabled={disabled}
            aria-pressed={active}
            onClick={() => onChange(days)}
            className={cn(
              'rounded-[7px] px-3 py-[5px] font-body text-sm font-semibold transition-colors duration-[120ms] ease-out focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none disabled:pointer-events-none disabled:opacity-50',
              active ? 'bg-surface-card text-body shadow-card' : 'text-secondary hover:text-body',
            )}
          >
            {days}d
          </button>
        )
      })}
    </span>
  )
}
