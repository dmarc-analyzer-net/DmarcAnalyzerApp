import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'

import { ConfigImportPanel } from '@/components/ConfigImportPanel'
import { Notice } from '@/components/Notice'
import { Button } from '@/components/ui/button'
import { Icon } from '@/components/ui/icon'
import { fetchJson } from '@/lib/api'
import type { ConfigImportPreview } from '@/lib/entities'
import { usePageTitle } from '@/lib/use-page-title'

/**
 * The first thing the first administrator does on a clean install: put the previous
 * install's configuration back.
 *
 * It is a step and not a shell mode because a recovery already has an operator at a
 * browser, and because the console can answer "is this a clean install?" itself. It
 * is a *page* rather than part of the bootstrap card because `LoginPage` signs the
 * new account in immediately, which flips the auth status and unmounts that card
 * mid-flow — see the hand-off comment in `LoginPage.handleBootstrap`.
 *
 * The clean-install signal comes from the import preview, not from
 * `GET /api/v1/auth/setup`: by the time this renders the bootstrap administrator
 * exists, so `requiresBootstrap` is already false and would say the opposite of the
 * truth.
 */
export function FirstRunImportPage() {
  usePageTitle('Restore configuration')
  const navigate = useNavigate()
  const [preview, setPreview] = useState<ConfigImportPreview | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  /**
   * The step is over once an import commits, so the skip route goes away. It has to:
   * if the import replaced this operator's password, navigating to the dashboard
   * fires requests that 401, and a 401 force-logs-out the console — over the top of
   * the one screen saying which credentials to use next.
   */
  const [imported, setImported] = useState(false)

  useEffect(() => {
    let cancelled = false

    const load = async () => {
      try {
        const payload = await fetchJson<ConfigImportPreview>(
          '/api/v1/admin/config/import/preview',
        )
        if (!cancelled) setPreview(payload)
      } catch (loadError) {
        if (!cancelled) {
          setError(
            loadError instanceof Error
              ? loadError.message
              : 'Could not check whether this install is empty',
          )
        }
      } finally {
        if (!cancelled) setBusy(false)
      }
    }

    void load()
    return () => {
      cancelled = true
    }
  }, [])

  return (
    <>
      <div className="mb-5 max-w-2xl">
        <h1 className="font-display text-xl font-bold tracking-tight text-body">
          Restore from a configuration export
        </h1>
        <p className="mt-1 text-sm text-secondary">
          {preview && !preview.isEmptyInstall
            ? 'This install already holds configuration, so a faithful restore is no longer possible here. Merging an export into it is.'
            : 'Your administrator account is ready. If you are recovering an install, import its configuration now — before adding anything by hand, while a faithful restore is still possible.'}
        </p>
      </div>

      {error ? <Notice tone="danger" className="mb-3.5">{error}</Notice> : null}

      {busy ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : null}

      {preview ? (
        <div className="grid gap-3.5">
          {!preview.isEmptyInstall ? (
            <Notice tone="warn" title="Restore mode is no longer available">
              Restore only accepts an install nothing has been added to yet — no clients of your
              own, no domains, no mailbox sources — because an import that never deletes cannot
              reproduce a state something was deleted from. Merge still works, and the{' '}
              <Link
                to="/backup"
                className="underline decoration-dotted underline-offset-2 hover:text-body"
              >
                backup page
              </Link>{' '}
              is where this lives from now on.
            </Notice>
          ) : null}

          <ConfigImportPanel preview={preview} onImported={() => setImported(true)} />

          {!imported ? (
            <div className="flex flex-wrap items-center gap-3">
              <Button type="button" variant="secondary" onClick={() => navigate('/dashboard')}>
                <Icon name="arrow-right" size={16} />
                Skip — set this install up by hand
              </Button>
              <span className="text-xs text-faint">
                Skipping changes nothing — this install already has a{' '}
                <span className="font-mono">default</span> client to add a domain or a mailbox
                source under. You can import later from Backup and recovery, though only in merge
                mode once you have added something.
              </span>
            </div>
          ) : null}
        </div>
      ) : null}
    </>
  )
}
