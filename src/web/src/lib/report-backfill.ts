import type { ReportBackfillState } from '@/lib/entities'

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
