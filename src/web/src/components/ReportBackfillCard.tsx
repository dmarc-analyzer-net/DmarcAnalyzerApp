import { useCallback, useEffect, useState } from 'react'

import { Notice } from '@/components/Notice'
import { StatusRow } from '@/components/StatusRow'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { Icon } from '@/components/ui/icon'
import { fetchJson } from '@/lib/api'
import type { ReportBackfillPreview, ReportBackfillState, ReportBackfillStatus } from '@/lib/entities'
import { formatRelativeOrDate, formatShortDate } from '@/lib/format'
import { describeBackfillCounts, plural } from '@/lib/report-backfill'

const STATUS_META: Record<ReportBackfillStatus, { label: string; badge: 'neutral' | 'warning' | 'success' | 'danger' }> = {
  idle: { label: 'Not run', badge: 'neutral' },
  requested: { label: 'Queued for the worker', badge: 'warning' },
  running: { label: 'Running', badge: 'warning' },
  completed: { label: 'Completed', badge: 'success' },
  failed: { label: 'Failed', badge: 'danger' },
}

/** How often the status row is re-read while the worker is on it. */
const POLL_MS = 5000

/**
 * The report backfill: fills the report-level fields on reports stored before those
 * fields existed, by re-reading their mail from the report-mail archive. Lives on the
 * backup page because the archive is what it reads, and whether the archive is on is the
 * first thing an operator needs to know about it.
 */
export function ReportBackfillCard({ disabled = false }: { disabled?: boolean }) {
  const [preview, setPreview] = useState<ReportBackfillPreview | null>(null)
  const [state, setState] = useState<ReportBackfillState | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [runError, setRunError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const [previewData, stateData] = await Promise.all([
        fetchJson<ReportBackfillPreview>('/api/v1/admin/report-backfill/preview'),
        fetchJson<ReportBackfillState>('/api/v1/admin/report-backfill/status'),
      ])
      setPreview(previewData)
      setState(stateData)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load the report backfill')
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  // The worker does the work on its own loop, so while a run is pending the row is
  // re-read on a timer; the preview is not, since nothing it counts changes until apply.
  const pending = state?.status === 'requested' || state?.status === 'running'
  useEffect(() => {
    if (!pending) return
    const timer = setInterval(() => {
      void fetchJson<ReportBackfillState>('/api/v1/admin/report-backfill/status')
        .then(setState)
        .catch(() => undefined)
    }, POLL_MS)
    return () => clearInterval(timer)
  }, [pending])

  const run = async () => {
    setRunError(null)
    try {
      const accepted = await fetchJson<ReportBackfillState>('/api/v1/admin/report-backfill/run', {
        method: 'POST',
      })
      setState(accepted)
    } catch (postError) {
      setRunError(postError instanceof Error ? postError.message : 'The backfill could not be queued')
    }
  }

  const canRun = Boolean(preview?.archiveConfigured) && !pending && !busy && !disabled

  return (
    <Card pad>
      <CardHeader
        title="Report backfill"
        description="Fills the report-level fields (format, np, testing, generator, errors, parser notes) on reports stored before they existed, by re-reading their mail from the report-mail archive."
        actions={
          <div className="flex items-center gap-2">
            <Button variant="secondary" size="sm" disabled={busy || disabled} onClick={() => void load()}>
              <Icon name="refresh-cw" size={14} className={busy ? 'animate-spin' : undefined} />
              Refresh
            </Button>
            <Button size="sm" disabled={!canRun} onClick={() => void run()}>
              Run backfill
            </Button>
          </div>
        }
      />

      {error ? (
        <Notice tone="danger" className="mb-3">
          {error}
        </Notice>
      ) : null}
      {runError ? (
        <Notice tone="warn" className="mb-3">
          {runError}
        </Notice>
      ) : null}
      {state?.status === 'failed' && state.lastError ? (
        <Notice tone="warn" title="The last run failed" className="mb-3">
          <span className="font-mono text-xs">{state.lastError}</span>{' '}
          Running again resumes where it stopped.
        </Notice>
      ) : null}

      {preview && state ? (
        <div className="grid">
          <StatusRow
            label="Archive"
            badge={
              preview.archiveConfigured ? (
                <Badge variant="success" dot>
                  Configured
                </Badge>
              ) : (
                <Badge variant="warning" dot>
                  Not configured
                </Badge>
              )
            }
            value={preview.archive ?? undefined}
            hint={
              preview.archiveConfigured
                ? `${plural(preview.archivedObjects, 'archived mail')} under ${preview.prefix}${
                    preview.oldestArchivedDay
                      ? `, ${formatShortDate(preview.oldestArchivedDay)} to ${formatShortDate(preview.newestArchivedDay ?? preview.oldestArchivedDay)}`
                      : ''
                  }.`
                : 'No bucket is configured, so there is no archive to read. Reports already stored stay as they are.'
            }
          />
          <StatusRow
            label="Reports without the fields"
            value={preview.reportsNotRecorded.toLocaleString('en-US')}
            hint={
              preview.reportsNotRecorded > 0
                ? `${plural(preview.reportsFromPushedSources, 'report')} from pushed sources were never archived; ` +
                  `${preview.reportsIngestedBeforeTheArchive.toLocaleString('en-US')} of the rest were ingested before the oldest archived day. ` +
                  `About ${preview.estimatedToStayUnrecorded.toLocaleString('en-US')} will stay "not recorded" whatever happens; the rest depends on what the archive holds.`
                : 'Every stored report already has them.'
            }
          />
          <StatusRow
            label="Last run"
            badge={
              <Badge variant={STATUS_META[state.status].badge}>
                {STATUS_META[state.status].label}
                {pending && state.pass ? ` · ${state.pass}` : ''}
              </Badge>
            }
            value={
              state.finishedAtUtc
                ? formatRelativeOrDate(state.finishedAtUtc)
                : state.requestedAtUtc
                  ? `requested ${formatRelativeOrDate(state.requestedAtUtc)}`
                  : undefined
            }
            hint={
              state.status === 'idle' ? (
                'Nothing has run yet. The run is queued for the worker and walks the whole archive once; a report is only written when every archived copy of it agrees.'
              ) : (
                <span className="font-mono">{describeBackfillCounts(state)}</span>
              )
            }
          />
        </div>
      ) : busy ? (
        <div className="flex items-center gap-2 py-4 text-sm text-secondary">
          <Icon name="loader-circle" size={16} className="animate-spin" />
          Counting the archive…
        </div>
      ) : null}
    </Card>
  )
}
