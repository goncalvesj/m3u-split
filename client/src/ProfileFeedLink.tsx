import { useEffect, useId, useRef, useState } from 'react'
import { OnlineApiError } from './online-api'
import type { OnlineAuth } from './online-auth'
import type { OnlineConfiguration } from './online-config'
import {
  createFeedLink,
  describeFeedFailure,
  feedUrl,
  getFeedLink,
  OnlineFeedError,
  regenerateFeedLink,
  revokeFeedLink,
} from './online-feeds'
import type { OnlineFeedLink } from './online-feeds'
import { formatTime, needsInteraction } from './online-playlist-utils'
import type { OnlineProfile } from './online-profiles'

interface ProfileFeedLinkProps {
  profile: OnlineProfile
  config: OnlineConfiguration
  auth: OnlineAuth
  /** Reports a rejected session so the parent can ask the user to sign in or check access again. */
  onAccessError: (error: unknown) => void
  /** Called when the API reports that the profile no longer exists. */
  onMissing: () => void
}

type FeedView =
  | { status: 'loading' }
  | { status: 'none' }
  | { status: 'ready'; link: OnlineFeedLink }
  | { status: 'failed'; message: string }

type FeedAction = 'load' | 'create' | 'regenerate' | 'revoke'
type Confirm = 'regenerate' | 'revoke'
type FocusTarget = 'toggle' | 'url' | 'create' | 'retry' | Confirm

const WARNING =
  "Anyone who has the TV link can download this playlist, including your provider's stream credentials. Keep it " +
  'private, and regenerate it if it is shared by accident.'

const isAccessError = (error: unknown) => error instanceof OnlineApiError || needsInteraction(error)

const describeError = (error: unknown) =>
  error instanceof OnlineFeedError || error instanceof OnlineApiError
    ? error.message
    : describeFeedFailure('token_unavailable')

/** The secret TV link of one saved profile. Nothing is requested until the user opens it. */
export function ProfileFeedLink({ profile, config, auth, onAccessError, onMissing }: ProfileFeedLinkProps) {
  const id = useId()
  const profileId = profile.id
  const [open, setOpen] = useState(false)
  const [view, setView] = useState<FeedView>({ status: 'loading' })
  const [busy, setBusy] = useState<FeedAction | null>(null)
  const [confirm, setConfirm] = useState<Confirm | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const request = useRef<AbortController | null>(null)
  const pendingFocus = useRef<FocusTarget | null>(null)
  const toggleButton = useRef<HTMLButtonElement>(null)
  const urlField = useRef<HTMLInputElement>(null)
  const createButton = useRef<HTMLButtonElement>(null)
  const regenerateButton = useRef<HTMLButtonElement>(null)
  const revokeButton = useRef<HTMLButtonElement>(null)
  const retryButton = useRef<HTMLButtonElement>(null)

  useEffect(() => () => request.current?.abort(), [])

  // Waits until no request runs, because buttons are disabled meanwhile and cannot take focus.
  useEffect(() => {
    const target = pendingFocus.current
    if (target === null || busy !== null) return
    const elements = {
      toggle: toggleButton,
      url: urlField,
      create: createButton,
      retry: retryButton,
      regenerate: regenerateButton,
      revoke: revokeButton,
    }
    const element = elements[target].current
    if (!element) return
    pendingFocus.current = null
    element.focus()
  }, [open, view, busy, confirm])

  const begin = (kind: FeedAction) => {
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    setBusy(kind)
    setError('')
    setNotice('')
    return controller
  }

  const finish = (controller: AbortController) => {
    if (request.current !== controller) return
    request.current = null
    setBusy(null)
  }

  const load = async (focusResult: boolean) => {
    const controller = begin('load')
    setView({ status: 'loading' })
    try {
      const credentials = await auth.acquireToken()
      if (controller.signal.aborted) return
      const link = await getFeedLink(config, credentials.accessToken, profileId, controller.signal)
      if (controller.signal.aborted) return
      setView(link ? { status: 'ready', link } : { status: 'none' })
      if (focusResult) pendingFocus.current = link ? 'url' : 'create'
    } catch (failure) {
      if (controller.signal.aborted) return
      if (failure instanceof OnlineFeedError && failure.kind === 'not-found') {
        onMissing()
        return
      }
      setView({ status: 'failed', message: describeError(failure) })
      if (isAccessError(failure)) onAccessError(failure)
      else if (focusResult) pendingFocus.current = 'retry'
    } finally {
      finish(controller)
    }
  }

  const report = (failure: unknown) => {
    if (isAccessError(failure)) {
      onAccessError(failure)
    } else if (failure instanceof OnlineFeedError && failure.kind === 'not-found') {
      onMissing()
    } else {
      if (failure instanceof OnlineFeedError && failure.code === 'feed_not_found') {
        // Another window revoked the link, so there is nothing left to regenerate.
        setView({ status: 'none' })
        setConfirm(null)
        pendingFocus.current = 'create'
      }
      setError(describeError(failure))
    }
  }

  const run = async (
    kind: Exclude<FeedAction, 'load'>,
    work: (accessToken: string, signal: AbortSignal) => Promise<void>,
  ) => {
    if (busy) return
    const controller = begin(kind)
    try {
      const credentials = await auth.acquireToken()
      if (controller.signal.aborted) return
      await work(credentials.accessToken, controller.signal)
    } catch (failure) {
      if (!controller.signal.aborted) report(failure)
    } finally {
      finish(controller)
    }
  }

  const create = () => run('create', async (accessToken, signal) => {
    const link = await createFeedLink(config, accessToken, profileId, signal)
    if (signal.aborted) return
    setView({ status: 'ready', link })
    setNotice('TV link ready. Copy it and paste it into Smart IPTV My List.')
    pendingFocus.current = 'url'
  })

  const regenerate = () => run('regenerate', async (accessToken, signal) => {
    const link = await regenerateFeedLink(config, accessToken, profileId, signal)
    if (signal.aborted) return
    setView({ status: 'ready', link })
    setConfirm(null)
    setNotice('New TV link created. The old link no longer works, so paste the new link into Smart IPTV My List.')
    pendingFocus.current = 'url'
  })

  const revoke = () => run('revoke', async (accessToken, signal) => {
    await revokeFeedLink(config, accessToken, profileId, signal)
    if (signal.aborted) return
    setView({ status: 'none' })
    setConfirm(null)
    setNotice('TV link revoked. It no longer works on any device.')
    pendingFocus.current = 'create'
  })

  const copy = async (url: string) => {
    setError('')
    setNotice('')
    try {
      await navigator.clipboard.writeText(url)
      setNotice('TV link copied. Paste it into Smart IPTV My List.')
    } catch {
      urlField.current?.focus()
      urlField.current?.select()
      setNotice('This browser did not allow copying. The link is selected in the field above, so copy it manually.')
    }
  }

  const close = () => {
    if (busy === 'load') {
      request.current?.abort()
      request.current = null
      setBusy(null)
    }
    setOpen(false)
    setConfirm(null)
    setError('')
    setNotice('')
  }

  const toggle = () => {
    if (open) {
      close()
      return
    }
    setOpen(true)
    setConfirm(null)
    void load(false)
  }

  const closeConfirm = () => {
    if (confirm) pendingFocus.current = confirm
    setConfirm(null)
    setError('')
  }

  const toggleConfirm = (kind: Confirm) => {
    if (confirm === kind) {
      closeConfirm()
      return
    }
    setConfirm(kind)
    setError('')
    setNotice('')
  }

  const actionBusy = busy !== null && busy !== 'load'
  const panelId = `${id}-panel`
  const confirmId = `${id}-confirm`
  const confirmState = (kind: Confirm) => ({
    'aria-expanded': confirm === kind,
    'aria-controls': confirm === kind ? confirmId : undefined,
  })
  const notReady = !profile.output && (
    <p className="online-profile-meta">
      The link starts working after the first successful refresh of this profile.
    </p>
  )

  return (
    <div className="online-feed">
      <div className="online-actions">
        <button
          ref={toggleButton}
          type="button"
          className="btn-small"
          disabled={actionBusy}
          aria-expanded={open}
          aria-controls={open ? panelId : undefined}
          onClick={toggle}
        >
          TV link
        </button>
      </div>

      {open && (
        <div
          id={panelId}
          className="online-profile-panel"
          role="group"
          aria-labelledby={`${id}-title`}
          onKeyDown={event => {
            if (event.key !== 'Escape' || actionBusy) return
            event.stopPropagation()
            if (confirm) {
              closeConfirm()
              return
            }
            pendingFocus.current = 'toggle'
            close()
          }}
        >
          <h5 id={`${id}-title`}>TV link for {profile.name}</h5>

          {view.status === 'failed' && (
            <>
              <p role="alert" className="online-error">{view.message}</p>
              <div className="online-actions">
                <button ref={retryButton} type="button" className="btn-small" onClick={() => void load(true)}>
                  Try again
                </button>
              </div>
            </>
          )}

          {view.status === 'none' && (
            <div className="online-profile-form">
              <p className="online-profile-meta">
                A TV link lets Smart IPTV load this profile directly. Paste it once into Smart IPTV My List as an
                external playlist link, and your TV always loads the latest successful refresh. The link works
                without sign-in.
              </p>
              {notReady}
              <p className="online-profile-meta online-feed-warning">{WARNING}</p>
              <div className="online-actions">
                <button
                  ref={createButton}
                  type="button"
                  className="btn-small"
                  disabled={actionBusy}
                  onClick={() => void create()}
                >
                  {busy === 'create' ? 'Creating TV link...' : 'Create TV link'}
                </button>
              </div>
            </div>
          )}

          {view.status === 'ready' && (
            <div className="online-profile-form">
              <label htmlFor={`${id}-url`}>TV link for Smart IPTV My List</label>
              <input
                id={`${id}-url`}
                ref={urlField}
                className="online-profile-input online-feed-url"
                type="text"
                readOnly
                spellCheck={false}
                value={feedUrl(config, view.link.token)}
                aria-describedby={`${id}-help`}
                onFocus={event => event.currentTarget.select()}
              />
              <p id={`${id}-help`} className="online-profile-meta">
                Paste this link once into Smart IPTV My List as an external playlist link. Your TV then always loads
                the latest successful refresh of this profile. The link works without sign-in.
              </p>
              {notReady}
              <p className="online-profile-meta online-feed-warning">{WARNING}</p>
              {view.link.createdAt !== null && (
                <p className="online-profile-meta">Created {formatTime(view.link.createdAt)}.</p>
              )}
              <div className="online-actions" role="group" aria-label={`TV link actions for ${profile.name}`}>
                <button
                  type="button"
                  className="btn-small"
                  disabled={actionBusy}
                  onClick={() => void copy(feedUrl(config, view.link.token))}
                >
                  Copy link
                </button>
                <button
                  ref={regenerateButton}
                  type="button"
                  className="btn-small"
                  disabled={actionBusy}
                  {...confirmState('regenerate')}
                  onClick={() => toggleConfirm('regenerate')}
                >
                  Regenerate link
                </button>
                <button
                  ref={revokeButton}
                  type="button"
                  className="btn-small"
                  disabled={actionBusy}
                  {...confirmState('revoke')}
                  onClick={() => toggleConfirm('revoke')}
                >
                  Revoke link
                </button>
              </div>
              {confirm && (
                <div id={confirmId} className="online-feed-confirm" role="group" aria-labelledby={`${id}-confirm-text`}>
                  <p id={`${id}-confirm-text`} className="online-profile-meta online-feed-warning">
                    {confirm === 'regenerate'
                      ? 'Regenerate the TV link? The current link stops working immediately, so you must paste the ' +
                        'new link into Smart IPTV My List before your TV can load this playlist again.'
                      : 'Revoke the TV link? It stops working immediately and your TV can no longer load this ' +
                        'playlist. You can create a new link later.'}
                  </p>
                  <div className="online-actions">
                    <button
                      type="button"
                      className="btn-small online-profile-danger"
                      disabled={actionBusy}
                      onClick={() => void (confirm === 'regenerate' ? regenerate() : revoke())}
                    >
                      {confirm === 'regenerate'
                        ? busy === 'regenerate' ? 'Regenerating...' : 'Regenerate now'
                        : busy === 'revoke' ? 'Revoking...' : 'Revoke now'}
                    </button>
                    <button type="button" className="btn-small" autoFocus disabled={actionBusy} onClick={closeConfirm}>
                      Cancel
                    </button>
                  </div>
                </div>
              )}
            </div>
          )}

          {error && <p role="alert" className="online-error">{error}</p>}
          <p className="online-status online-feed-notice" role="status" aria-atomic="true">
            {busy === 'load' ? 'Loading TV link...' : notice}
          </p>
        </div>
      )}
    </div>
  )
}
