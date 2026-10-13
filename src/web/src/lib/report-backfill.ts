import type { ReportBackfillPreview, ReportBackfillState } from '@/lib/entities'

export function plural(n: number, word: string): string {
  return `${n.toLocaleString('en-US')} ${word}${n === 1 ? '' : 's'}`
}

/** What a backfill run did, in one line: the counts that matter, in the order they happen. */
export function describeBackfillCounts(state: ReportBackfillState): string {
  return [
    plural(state.objectsSeen, 'archived mail'),
    plural(state.payloadsParsed, 'report'),
    `${state.matched.toLocaleString('en-US')} matched`,
    `${state.updated.toLocaleString('en-US')} updated`,
    `${state.skipped.toLocaleString('en-US')} skipped`,
    `${state.ambiguous.toLocaleString('en-US')} ambiguous`,
    `${state.failed.toLocaleString('en-US')} failed`,
  ].join(' · ')
}

/**
 * The hint under "Reports without the fields": why they lack them and how many a run can
 * still fill. Without an archive there is nothing to re-read, so the archive-relative
 * breakdown (pushed sources, "before the oldest archived day") would describe an archive
 * that does not exist; and a clause whose count is zero is left out rather than read as
 * "0 reports … were never archived".
 */
export function describeReportsWithoutFields(preview: ReportBackfillPreview): string {
  const n = preview.reportsNotRecorded
  if (n <= 0) return 'Every stored report already has them.'
  if (!preview.archiveConfigured) {
    return `There is no archive to re-read, so ${n === 1 ? 'it stays' : 'all of them stay'} "not recorded". Reports ingested from now on get the fields when they arrive.`
  }

  const reasons: string[] = []
  if (preview.reportsFromPushedSources > 0) {
    reasons.push(`${plural(preview.reportsFromPushedSources, 'report')} from pushed sources ${preview.reportsFromPushedSources === 1 ? 'was' : 'were'} never archived`)
  }
  if (preview.reportsIngestedBeforeTheArchive > 0) {
    reasons.push(`${plural(preview.reportsIngestedBeforeTheArchive, 'report')} ${preview.reportsIngestedBeforeTheArchive === 1 ? 'was' : 'were'} ingested before the oldest archived day`)
  }
  const why = reasons.length > 0 ? `${reasons.join('; ')}. ` : ''

  const stay = preview.estimatedToStayUnrecorded
  if (stay >= n) return `${why}All of them will stay "not recorded".`
  if (stay <= 0) return `${why}Whether they can be filled depends on what the archive holds.`
  return `${why}About ${stay.toLocaleString('en-US')} will stay "not recorded" whatever happens; the rest depends on what the archive holds.`
}
