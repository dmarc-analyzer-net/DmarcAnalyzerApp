import { ANALYTICS_DAYS_OPTIONS, type AnalyticsDays } from '@/lib/analytics'
import { cn } from '@/lib/utils'

type DaysSelectorProps = {
  value: AnalyticsDays
  onChange: (days: AnalyticsDays) => void
  disabled?: boolean
}

/** Preset time-window selector (7 / 30 / 90 days) shared by analytics pages. */
export function DaysSelector({ value, onChange, disabled }: DaysSelectorProps) {
  return (
    <div
      role="group"
      aria-label="Time window"
      className="inline-flex shrink-0 items-center rounded-md border border-input bg-card p-0.5"
    >
      {ANALYTICS_DAYS_OPTIONS.map((days) => (
        <button
          key={days}
          type="button"
          disabled={disabled}
          aria-pressed={value === days}
          onClick={() => onChange(days)}
          className={cn(
            'h-8 rounded-[0.45rem] px-3 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50',
            value === days
              ? 'bg-primary text-primary-foreground'
              : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
          )}
        >
          {days} days
        </button>
      ))}
    </div>
  )
}
