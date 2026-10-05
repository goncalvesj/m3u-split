import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { loadFavourites, matchFavourites, saveFavourites } from './favourites'
import { OnlineApiError } from './online-api'
import type { OnlineAuth } from './online-auth'
import type { OnlineConfiguration } from './online-config'
import {
  checkSourceUrl,
  describeImportFailure,
  discardImport,
  downloadImportPlaylist,
  formatBytes,
  formatCount,
  formatTime,
  getImportCategories,
  getImportStatus,
  IMPORT_FILE_NAME,
  needsInteraction,
  OnlineImportError,
  startImport,
} from './online-imports'
import type { OnlineImportCategory, OnlineImportStatus } from './online-imports'

interface OnlineImportProps {
  config: OnlineConfiguration
  auth: OnlineAuth
  /** True only while the online API has verified access for the signed-in account. */
  active: boolean
  /** Reports a rejected session so the parent can ask the user to sign in or check access again. */
  onAccessError: (error: unknown) => void
}

type Action = 'start' | 'download' | 'discard'
type Problem = { message: string; retry: boolean }
type Note = { text: string; error: boolean }
type ImportedCategories = { id: string; categories: OnlineImportCategory[]; entries: number }

const FIRST_POLL_MS = 1_000
const MAX_POLL_MS = 5_000
const FIRST_RETRY_MS = 2_000
const MAX_RETRY_MS = 10_000
const MAX_POLL_FAILURES = 3
const REVOKE_DOWNLOAD_MS = 60_000

const plural = (count: number, one: string, many: string) => `${formatCount(count)} ${count === 1 ? one : many}`

function pause(ms: number, signal: AbortSignal) {
  return new Promise<void>(resolve => {
    const timer = window.setTimeout(resolve, ms)
    signal.addEventListener('abort', () => {
      window.clearTimeout(timer)
      resolve()
    }, { once: true })
  })
}

function describeProgress(status: OnlineImportStatus): string {
  switch (status.status) {
    case 'queued':
      return 'Import queued. The server will start downloading the playlist shortly.'
    case 'running':
      return status.stage === 'indexing'
        ? 'Reading categories from the downloaded playlist...'
        : 'Downloading the playlist on the server...'
    case 'ready':
      return `Import ready: ${plural(status.categoryCount, 'category', 'categories')} with ${plural(status.entries, 'channel', 'channels')}.`
    case 'failed':
      return 'Import failed.'
  }
}

function describeTransfer(status: OnlineImportStatus): string {
  if (status.status === 'queued' || (!status.bytesDownloaded && !status.entries)) return ''
  const indexing = status.status === 'running' && status.stage === 'indexing'
  const downloaded = status.status === 'running' && !indexing && status.totalBytes
    ? `${formatBytes(status.bytesDownloaded)} of ${formatBytes(status.totalBytes)} downloaded`
    : `${formatBytes(status.bytesDownloaded)} downloaded`
  return indexing ? `${downloaded}, ${plural(status.entries, 'channel', 'channels')} read so far` : downloaded
}

/** Imports a provider playlist on the server and downloads a filtered copy. Shown only while access is verified. */
export default function OnlineImport({ config, auth, active, onAccessError }: OnlineImportProps) {
  const id = useId()
  const [url, setUrl] = useState('')
  const [formError, setFormError] = useState('')
  const [current, setCurrent] = useState<OnlineImportStatus | null>(null)
  const [imported, setImported] = useState<ImportedCategories | null>(null)
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set())
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState<Action | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [notice, setNotice] = useState('')
  const [note, setNote] = useState<Note | null>(null)
  const [tracking, setTracking] = useState<{ delay: number } | null>(null)
  const action = useRef<AbortController | null>(null)
  // Changes whenever the tracked import is replaced or cleared, so late poll results are dropped.
  const generation = useRef(0)
  const importId = current?.id ?? null

  const clearImport = useCallback((outcome: { problem?: string; notice?: string }) => {
    generation.current += 1
    setCurrent(null)
    setImported(null)
    setSelected(new Set())
    setSearch('')
    setNote(null)
    setTracking(null)
    setProblem(outcome.problem ? { message: outcome.problem, retry: false } : null)
    setNotice(outcome.notice ?? '')
  }, [])

  useEffect(() => {
    if (!active) return
    return () => action.current?.abort()
  }, [active])

  useEffect(() => {
    if (!active || !importId || !tracking) return
    const controller = new AbortController()
    const started = generation.current
    const stale = () => controller.signal.aborted || generation.current !== started

    const track = async () => {
      let delay = tracking.delay
      let failures = 0
      for (;;) {
        await pause(delay, controller.signal)
        if (stale()) return
        try {
          const credentials = await auth.acquireToken()
          if (stale()) return
          const status = await getImportStatus(config, credentials.accessToken, importId, controller.signal)
          if (stale()) return
          setCurrent(status)
          if (status.status === 'failed') {
            setTracking(null)
            return
          }
          if (status.status === 'ready') {
            const fresh = await auth.acquireToken()
            if (stale()) return
            const result = await getImportCategories(config, fresh.accessToken, importId, controller.signal)
            if (stale()) return
            setImported({ id: importId, ...result })
            setTracking(null)
            return
          }
          failures = 0
          delay = Math.min(Math.max(delay * 1.5, FIRST_POLL_MS), MAX_POLL_MS)
        } catch (error) {
          if (stale()) return
          if (error instanceof OnlineApiError || needsInteraction(error)) {
            // Tracking stays set, so polling resumes once access is verified again.
            onAccessError(error)
            return
          }
          if (error instanceof OnlineImportError && error.kind !== 'retryable') {
            if (error.kind === 'rejected') {
              setProblem({ message: error.message, retry: false })
              setTracking(null)
            } else {
              clearImport({ problem: error.message })
            }
            return
          }
          failures += 1
          if (failures >= MAX_POLL_FAILURES) {
            setProblem({
              message: error instanceof OnlineImportError ? error.message : describeImportFailure('token_unavailable'),
              retry: true,
            })
            setTracking(null)
            return
          }
          delay = Math.min(Math.max(delay * 2, FIRST_RETRY_MS), MAX_RETRY_MS)
        }
      }
    }

    void track()
    return () => controller.abort()
  }, [active, importId, tracking, auth, config, onAccessError, clearImport])

  if (!active) return null

  const ready = current?.status === 'ready' && imported?.id === current.id ? imported : null
  const categories = ready?.categories ?? []
  const query = search.toLowerCase()
  const filtered = query ? categories.filter(category => category.name.toLowerCase().includes(query)) : categories
  const selectableCount = categories.filter(category => category.count > 0).length
  const chosen = categories.filter(category => category.count > 0 && selected.has(category.name))
  const chosenChannels = chosen.reduce((total, category) => total + category.count, 0)
  const failure = current?.status === 'failed' ? current.error ?? { code: 'internal_error', httpStatus: null } : null
  const transfer = current ? describeTransfer(current) : ''
  const inProgress = current?.status === 'queued' || current?.status === 'running'
  const knownTotal = current?.status === 'running' && current.stage !== 'indexing' && current.totalBytes
    ? current.totalBytes
    : null

  const begin = (kind: Action) => {
    action.current?.abort()
    const controller = new AbortController()
    action.current = controller
    setBusy(kind)
    setNotice('')
    return controller
  }

  const finish = (controller: AbortController) => {
    if (action.current !== controller) return
    action.current = null
    setBusy(null)
  }

  const report = (error: unknown, show: (message: string) => void) => {
    if (error instanceof OnlineApiError || needsInteraction(error)) {
      onAccessError(error)
    } else if (error instanceof OnlineImportError && (error.kind === 'not-found' || error.kind === 'expired')) {
      clearImport({ problem: error.message })
    } else {
      show(error instanceof OnlineImportError ? error.message : describeImportFailure('token_unavailable'))
    }
  }

  const start = async () => {
    if (busy) return
    const invalid = checkSourceUrl(url)
    setFormError(invalid)
    if (invalid) return
    const controller = begin('start')
    try {
      const credentials = await auth.acquireToken()
      if (controller.signal.aborted) return
      const status = await startImport(config, credentials.accessToken, url.trim(), controller.signal)
      if (controller.signal.aborted) return
      generation.current += 1
      setCurrent(status)
      setImported(null)
      setSelected(new Set())
      setSearch('')
      setNote(null)
      setProblem(null)
      setUrl('')
      setTracking(status.status === 'failed' ? null : { delay: status.status === 'ready' ? 0 : FIRST_POLL_MS })
    } catch (error) {
      if (!controller.signal.aborted) report(error, setFormError)
    } finally {
      finish(controller)
    }
  }

  const discard = async () => {
    if (busy || !current) return
    const target = current.id
    const unfinished = current.status === 'queued' || current.status === 'running'
    const controller = begin('discard')
    // Pause polling so a late status result cannot report the import as missing while it is being deleted.
    generation.current += 1
    setTracking(null)
    setProblem(null)
    let discarded = false
    try {
      const credentials = await auth.acquireToken()
      if (controller.signal.aborted) return
      await discardImport(config, credentials.accessToken, target, controller.signal)
      discarded = true
      if (controller.signal.aborted) return
      clearImport({ notice: 'Import discarded. Its data was deleted from the server.' })
    } catch (error) {
      if (!controller.signal.aborted) report(error, message => setProblem({ message, retry: false }))
    } finally {
      if (!discarded && unfinished) setTracking({ delay: FIRST_POLL_MS })
      finish(controller)
    }
  }

  const download = async () => {
    if (busy || !ready || !chosen.length) return
    const target = ready.id
    const names = chosen.map(category => category.name)
    const summary = `${plural(names.length, 'category', 'categories')}, ${plural(chosenChannels, 'channel', 'channels')}`
    const controller = begin('download')
    setProblem(null)
    try {
      const credentials = await auth.acquireToken()
      if (controller.signal.aborted) return
      const playlist = await downloadImportPlaylist(config, credentials.accessToken, target, names, controller.signal)
      if (controller.signal.aborted) return
      const href = URL.createObjectURL(playlist)
      const link = document.createElement('a')
      link.href = href
      link.download = IMPORT_FILE_NAME
      link.hidden = true
      document.body.append(link)
      link.click()
      link.remove()
      window.setTimeout(() => URL.revokeObjectURL(href), REVOKE_DOWNLOAD_MS)
      setNotice(`Downloaded ${IMPORT_FILE_NAME} (${summary}). Upload this smaller file to your TV app.`)
    } catch (error) {
      if (!controller.signal.aborted) report(error, message => setProblem({ message, retry: false }))
    } finally {
      finish(controller)
    }
  }

  const toggle = (name: string) => {
    setSelected(previous => {
      const next = new Set(previous)
      if (next.has(name)) {
        next.delete(name)
      } else {
        next.add(name)
      }
      return next
    })
    setNote(null)
  }

  const selectAll = () => {
    setSelected(new Set(filtered.filter(category => category.count > 0).map(category => category.name)))
    setNote(null)
  }

  const selectNone = () => {
    setSelected(new Set())
    setNote(null)
  }

  const applyFavourites = () => {
    const saved = loadFavourites()
    if (saved.error) {
      setNote({ text: saved.error, error: true })
      return
    }
    if (!saved.categories.length) {
      setNote({ text: 'No favourites are saved in this browser yet. Select categories, then save them as favourites.', error: false })
      return
    }
    const available = categories.filter(category => category.count > 0).map(category => category.name)
    const { matched, missing } = matchFavourites(saved.categories, available)
    if (!matched.length) {
      setNote({ text: 'None of your favourites are available in this import. Your current selection is unchanged.', error: false })
      return
    }
    setSelected(new Set(matched))
    setNote({
      text: `${plural(matched.length, 'favourite', 'favourites')} selected` +
        (missing.length ? `; ${formatCount(missing.length)} unavailable in this import (kept in your favourites).` : '.'),
      error: false,
    })
  }

  const storeFavourites = () => {
    const names = chosen.map(category => category.name)
    const error = saveFavourites(names)
    setNote(error
      ? { text: error, error: true }
      : { text: `${plural(names.length, 'favourite', 'favourites')} saved. Local playlists in this browser use the same favourites.`, error: false })
  }

  return (
    <div className="online-import">
      <h3>Import from URL</h3>
      <p className="online-hint">
        Paste the M3U playlist link from your IPTV provider. The server downloads it and lists its categories, so the
        full playlist never reaches this device. Then download a smaller playlist with only the categories you choose.
      </p>
      <form
        className="online-import-form"
        noValidate
        onSubmit={event => {
          event.preventDefault()
          void start()
        }}
      >
        <label htmlFor={`${id}-url`}>Provider playlist URL</label>
        <div className="online-import-row">
          <input
            id={`${id}-url`}
            className="online-import-input"
            type="url"
            inputMode="url"
            autoComplete="off"
            autoCapitalize="off"
            autoCorrect="off"
            spellCheck={false}
            required
            placeholder="http://provider.example:8080/get.php?..."
            value={url}
            aria-invalid={formError ? true : undefined}
            aria-describedby={formError ? `${id}-url-help ${id}-url-error` : `${id}-url-help`}
            onChange={event => {
              setUrl(event.target.value)
              setFormError('')
            }}
          />
          <button type="submit" className="btn-small" disabled={busy !== null}>
            {busy === 'start' ? 'Starting import...' : 'Import playlist'}
          </button>
        </div>
        <p id={`${id}-url-help`} className="online-import-meta">
          The URL, including any credentials in it, is sent only to the online API and kept privately with this import
          until it is discarded or expires. It is not saved in this browser. Starting a new import replaces your current one.
        </p>
        {formError && <p id={`${id}-url-error`} role="alert" className="online-error">{formError}</p>}
      </form>

      <p className="online-status online-import-status" role="status" aria-label="URL import" aria-atomic="true">
        {current ? describeProgress(current) : ''}
      </p>
      {current && (
        <div className="online-import-current">
          {current.source && <p className="online-import-meta">Source: <strong>{current.source}</strong></p>}
          {inProgress && (
            <progress
              className="online-import-progress"
              aria-label="Server import progress"
              value={knownTotal ? Math.min(current.bytesDownloaded, knownTotal) : undefined}
              max={knownTotal ?? undefined}
            />
          )}
          {transfer && <p className="online-import-meta">{transfer}</p>}
          {failure && (
            <p role="alert" className="online-error">
              {describeImportFailure(failure.code, failure.httpStatus, current.maxBytes)}
            </p>
          )}
          {current.expiresAt !== null && (
            <p className="online-import-meta">
              The imported playlist is kept on the server until {formatTime(current.expiresAt)} unless you discard it or
              start a new import.
            </p>
          )}
          <div className="online-actions">
            <button type="button" className="btn-small" disabled={busy !== null} onClick={() => void discard()}>
              {busy === 'discard' ? 'Discarding import...' : 'Discard import'}
            </button>
          </div>
        </div>
      )}

      {problem && (
        <div className="online-import-problem">
          <p role="alert" className="online-error">{problem.message}</p>
          {problem.retry && current && (
            <div className="online-actions">
              <button
                type="button"
                className="btn-small"
                onClick={() => {
                  setProblem(null)
                  setTracking({ delay: 0 })
                }}
              >
                Check import status again
              </button>
            </div>
          )}
        </div>
      )}

      {ready && (
        <div className="online-import-picker">
          <div className="online-import-heading">
            <h4 id={`${id}-categories`}>Choose imported categories</h4>
            <span className="badge">{chosen.length} / {selectableCount}</span>
          </div>

          <div className="favourites" role="group" aria-labelledby={`${id}-favourites`}>
            <h4 id={`${id}-favourites`}>Favourites</h4>
            <p className="favourites-description" id={`${id}-favourites-description`}>
              Favourites are category names saved in this browser and shared with local playlists. Applying them replaces
              your current selection.
            </p>
            <div className="favourites-actions">
              <button
                type="button"
                className="btn-small favourites-use"
                onClick={applyFavourites}
                disabled={busy !== null}
                aria-describedby={`${id}-favourites-description`}
              >
                Apply favourites
              </button>
              <button
                type="button"
                className="btn-small"
                onClick={storeFavourites}
                disabled={!chosen.length || busy !== null}
                title="Replace your saved favourites with the selected imported categories"
              >
                Save as favourites
              </button>
            </div>
            <p className="favourites-status" role="status" aria-atomic="true">{note && !note.error ? note.text : ''}</p>
            {note?.error && <p className="message favourites-error" role="alert">{note.text}</p>}
          </div>

          <div className="category-toolbar">
            <div className="search-box">
              <span className="search-icon" aria-hidden="true">🔍</span>
              <input
                type="text"
                aria-label="Search imported categories"
                placeholder="Search imported categories..."
                autoComplete="off"
                value={search}
                onChange={event => setSearch(event.target.value)}
              />
              {search && (
                <button
                  type="button"
                  className="search-clear"
                  aria-label="Clear imported category search"
                  onClick={() => setSearch('')}
                >
                  ✕
                </button>
              )}
            </div>
            <div className="select-actions">
              <button type="button" className="btn-small" aria-label="Select all imported categories" onClick={selectAll}>
                Select all
              </button>
              <button type="button" className="btn-small" aria-label="Select none of the imported categories" onClick={selectNone}>
                Select none
              </button>
            </div>
          </div>

          <div className="categories" role="group" aria-labelledby={`${id}-categories`}>
            {!filtered.length ? (
              <div className="empty-state">
                <p>No imported categories match "{search}"</p>
              </div>
            ) : (
              filtered.map(category => (
                <label
                  key={category.name}
                  className={`category-item${selected.has(category.name) ? ' selected' : ''}${category.count ? '' : ' online-import-empty'}`}
                >
                  <input
                    type="checkbox"
                    checked={selected.has(category.name)}
                    disabled={!category.count}
                    aria-label={`${category.name} (${plural(category.count, 'channel', 'channels')})`}
                    onChange={() => toggle(category.name)}
                  />
                  <span className="category-name">{category.name}</span>
                  <span className="online-import-count" aria-hidden="true">{formatCount(category.count)}</span>
                </label>
              ))
            )}
          </div>

          <p className="online-import-meta">
            {plural(chosen.length, 'category', 'categories')} selected with {plural(chosenChannels, 'channel', 'channels')}.
          </p>
          <button
            type="button"
            className="btn-primary online-import-download"
            onClick={() => void download()}
            disabled={!chosen.length || busy !== null}
          >
            <span aria-hidden="true">⬇️</span>
            {busy === 'download' ? 'Preparing filtered playlist...' : 'Download filtered playlist'}
          </button>
          {!chosen.length && <p className="hint">Select at least one imported category to download.</p>}
        </div>
      )}

      <p className="online-status online-import-notice" role="status" aria-atomic="true">{notice}</p>
    </div>
  )
}
