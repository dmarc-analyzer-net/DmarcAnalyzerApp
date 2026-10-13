import type { ReportNote, ReportNoteSeverity, ReportNotes } from '@/lib/analytics'

/** Badge tone per severity: info is how the report was read, warning changed something, error is a schema fault. */
export const NOTE_SEVERITY_BADGE: Record<ReportNoteSeverity, 'neutral' | 'warning' | 'danger'> = {
  info: 'neutral',
  warning: 'warning',
  error: 'danger',
}

export function noteCountLabel(report: ReportNote): string {
  if (report.warningCount === 0) {
    return report.messages.length === 1 ? '1 note' : `${report.messages.length} notes`
  }
  return report.warningCount === 1 ? '1 warning' : `${report.warningCount} warnings`
}

/**
 * What the list shows against what the window holds. Spelled out because the list is
 * honest only with its denominators: a cap, info-only reports it was not asked for, and
 * reports stored before notes were kept, which are not clean — they are unknown.
 */
export function reportNotesSummary(notes: ReportNotes): string {
  const parts: string[] = []
  parts.push(
    `${notes.reportsWithWarnings} of ${notes.reportsInWindow} reports in the window ${
      notes.reportsWithWarnings === 1 ? 'has' : 'have'
    } warnings`,
  )
  if (notes.reportsWithInfoOnly > 0) {
    parts.push(
      `${notes.reportsWithInfoOnly} ${notes.reportsWithInfoOnly === 1 ? 'has' : 'have'} informational notes only`,
    )
  }
  if (notes.reportsNotRecorded > 0) {
    parts.push(
      `${notes.reportsNotRecorded} ${
        notes.reportsNotRecorded === 1 ? 'was' : 'were'
      } stored before notes were recorded and cannot be shown`,
    )
  }
  const listed = notes.reports.length
  const eligible = notes.reportsWithWarnings + (notes.includeInfo ? notes.reportsWithInfoOnly : 0)
  const suffix = listed < eligible ? ` — showing the newest ${listed}` : ''
  return `${parts.join('; ')}${suffix}.`
}
