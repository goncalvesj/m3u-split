import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { loadFavourites, matchFavourites } from './favourites'
import { OnlineApiError } from './online-api'
import type { OnlineAuth } from './online-auth'
import type { OnlineConfiguration } from './online-config'
import { checkSourceUrl, formatBytes, formatCount, formatTime, needsInteraction } from './online-playlist-utils'
import {
  changeProfileSource,
  checkProfileName,
  createProfile,
  deleteProfile,
  describeProfileFailure,
  downloadProfileOutput,
  getProfileCategories,
  isRefreshActive,
  listProfiles,
  MAX_PROFILE_CATEGORIES,
  MAX_PROFILE_NAME_LENGTH,
  OnlineProfileError,
  profileFileName,
  refreshProfile,
  renameProfile,
  saveProfileCategories,
} from './online-profiles'
import type {
  NewOnlineProfile,
  OnlineProfile,
  OnlineProfileList,
  ProfileCategories,
  ProfileCategory,
  ProfileOutput,
  ProfileRefresh,
} from './online-profiles'
import { ProfileFeedLink } from './ProfileFeedLink'

interface OnlineProfilesProps {
  config: OnlineConfiguration
  auth: OnlineAuth
  /** True only while the online API has verified access for the signed-in account. */
  active: boolean
  /** Reports a rejected session so the parent can ask the user to sign in or check access again. */
  onAccessError: (error: unknown) => void
}

type Panel = 'categories' | 'rename' | 'source' | 'delete'
type Action = Panel | 'refresh' | 'download'
type ItemError = { origin: Action; code: string; message: string }
type Note = { text: string; error: boolean }

const FIRST_POLL_MS = 1_000
const MAX_POLL_MS = 5_000
const FIRST_RETRY_MS = 2_000
const MAX_RETRY_MS = 10_000
const MAX_POLL_FAILURES = 3
const REVOKE_DOWNLOAD_MS = 60_000
// These failures mean the profile changed on the server, so the list is loaded again to show its current state.
const STALE_CODES: ReadonlySet<string> = new Set([
  'refresh_in_progress',
  'profile_not_indexed',
  'output_not_found',
  'invalid_selection',
])

const plural = (count: number, one: string, many: string) => `${formatCount(count)} ${count === 1 ? one : many}`

const isAccessError = (error: unknown) => error instanceof OnlineApiError || needsInteraction(error)

const describeError = (error: unknown) =>
  error instanceof OnlineProfileError ? error.message : describeProfileFailure('token_unavailable')

function pause(ms: number, signal: AbortSignal) {
  return new Promise<void>(resolve => {
    const timer = window.setTimeout(resolve, ms)
    signal.addEventListener('abort', () => {
      window.clearTimeout(timer)
      resolve()
    }, { once: true })
  })
}

function describeRefresh(profile: OnlineProfile, maxBytes: number | null): string {
  const refresh = profile.refresh
  if (!refresh) return ''
  const when = refresh.finishedAt === null ? '' : ` on ${formatTime(refresh.finishedAt)}`
  switch (refresh.status) {
    case 'queued':
      return 'Refresh queued. The server will start downloading the playlist shortly.'
    case 'running':
      if (refresh.stage === 'indexing') return 'Reading categories from the downloaded playlist...'
      if (refresh.stage === 'filtering') return 'Building your filtered playlist...'
      return 'Downloading the playlist on the server...'
    case 'succeeded':
      return profile.selectedCount
        ? `Last refresh succeeded${when}.`
        : `Last refresh succeeded${when}. Choose categories to build your filtered playlist.`
    case 'failed': {
      const failure = refresh.error ?? { code: 'internal_error', httpStatus: null }
      const reason = describeProfileFailure(failure.code, failure.httpStatus, maxBytes)
      const kept = profile.output ? ' Your last filtered playlist is still available.' : ''
      return `Last refresh failed${when}. ${reason}${kept}`
    }
  }
}

function describeTransfer(refresh: ProfileRefresh | null): string {
  if (refresh?.status !== 'running' || !refresh.bytesDownloaded) return ''
  if (refresh.stage === 'filtering') {
    return refresh.entries === null ? '' : `${plural(refresh.entries, 'channel', 'channels')} in the downloaded playlist`
  }
  if (refresh.stage === 'indexing') {
    const downloaded = `${formatBytes(refresh.bytesDownloaded)} downloaded`
    return refresh.entries ? `${downloaded}, ${plural(refresh.entries, 'channel', 'channels')} read so far` : downloaded
  }
  return refresh.totalBytes
    ? `${formatBytes(refresh.bytesDownloaded)} of ${formatBytes(refresh.totalBytes)} downloaded`
    : `${formatBytes(refresh.bytesDownloaded)} downloaded`
}

function describeOutput(output: ProfileOutput | null): string {
  if (!output) return 'No filtered playlist yet.'
  const refreshed = output.refreshedAt === null ? '' : `, refreshed on ${formatTime(output.refreshedAt)}`
  return `Filtered playlist: ${plural(output.channels, 'channel', 'channels')} from ` +
    `${plural(output.includedCategoryCount, 'category', 'categories')}, ${formatBytes(output.sizeBytes)}${refreshed}.`
}

function describeSelection(profile: OnlineProfile): string {
  const selected = profile.selectedCount
    ? `${plural(profile.selectedCount, 'category', 'categories')} selected.`
    : 'No categories selected.'
  if (!profile.index) {
    return profile.selectedCount
      ? `${selected} Categories can be changed after the first refresh reads the playlist.`
      : 'Categories can be chosen after the first refresh reads the playlist.'
  }
  return `${selected} The latest playlist has ${plural(profile.index.categoryCount, 'category', 'categories')} ` +
    `with ${plural(profile.index.entries, 'channel', 'channels')}.`
}

function describePanel(panel: Panel, name: string): string {
  switch (panel) {
    case 'categories':
      return `Choose categories for ${name}`
    case 'rename':
      return `Rename ${name}`
    case 'source':
      return `Change the provider URL of ${name}`
    case 'delete':
      return `Delete ${name}?`
  }
}

/** Saved profiles: a provider URL and chosen categories that the server refreshes on request. Shown only while access is verified. */
export default function OnlineProfiles({ config, auth, active, onAccessError }: OnlineProfilesProps) {
  const id = useId()
  const [list, setList] = useState<OnlineProfileList | null>(null)
  const [problem, setProblem] = useState('')
  const [cycle, setCycle] = useState({ delay: 0 })
  const [adding, setAdding] = useState(false)
  const [notice, setNotice] = useState('')
  // Changes whenever a profile change is applied here, so an older list result never replaces it.
  const generation = useRef(0)
  const heading = useRef<HTMLHeadingElement>(null)
  const addButton = useRef<HTMLButtonElement>(null)
  const focusAdd = useRef(false)
  const focusTarget = useRef<string | null>(null)

  useEffect(() => {
    if (!active) return
    const controller = new AbortController()

    const poll = async () => {
      let delay = cycle.delay
      let failures = 0
      for (;;) {
        await pause(delay, controller.signal)
        if (controller.signal.aborted) return
        const started = generation.current
        try {
          const credentials = await auth.acquireToken()
          if (controller.signal.aborted) return
          const result = await listProfiles(config, credentials.accessToken, controller.signal)
          if (controller.signal.aborted) return
          failures = 0
          if (generation.current !== started) {
            // A profile changed here while the list was loading, so this result may be older. Load it again.
            delay = FIRST_POLL_MS
            continue
          }
          setList(result)
          setProblem('')
          if (!result.profiles.some(isRefreshActive)) return
          delay = Math.min(Math.max(delay * 1.5, FIRST_POLL_MS), MAX_POLL_MS)
        } catch (error) {
          if (controller.signal.aborted) return
          if (isAccessError(error)) {
            // The cycle is kept, so the list loads again once access is verified.
            onAccessError(error)
            return
          }
          failures += 1
          if ((error instanceof OnlineProfileError && error.kind !== 'retryable') || failures >= MAX_POLL_FAILURES) {
            setProblem(describeError(error))
            return
          }
          delay = Math.min(Math.max(delay * 2, FIRST_RETRY_MS), MAX_RETRY_MS)
        }
      }
    }

    void poll()
    return () => controller.abort()
  }, [active, cycle, auth, config, onAccessError])

  useEffect(() => {
    if (adding || !focusAdd.current) return
    focusAdd.current = false
    addButton.current?.focus()
  }, [adding])

  const update = useCallback((profile: OnlineProfile) => {
    generation.current += 1
    setList(previous => {
      if (!previous) return previous
      const known = previous.profiles.some(item => item.id === profile.id)
      const profiles = known
        ? previous.profiles.map(item => (item.id === profile.id ? profile : item))
        : [...previous.profiles, profile]
      return { ...previous, profiles }
    })
    if (isRefreshActive(profile)) setCycle({ delay: FIRST_POLL_MS })
  }, [])

  const reload = useCallback(() => setCycle({ delay: 0 }), [])

  const remove = useCallback((profileId: string, message: string) => {
    generation.current += 1
    setList(previous => previous && { ...previous, profiles: previous.profiles.filter(item => item.id !== profileId) })
    setNotice(message)
    heading.current?.focus()
  }, [])

  const takeFocus = useCallback((profileId: string) => {
    if (focusTarget.current !== profileId) return false
    focusTarget.current = null
    return true
  }, [])

  const created = useCallback((profile: OnlineProfile) => {
    focusTarget.current = profile.id
    update(profile)
    setAdding(false)
    setNotice(`Profile "${profile.name}" saved. Its first refresh has been queued on the server.`)
  }, [update])

  const limitReached = useCallback(() => {
    setAdding(false)
    setNotice(describeProfileFailure('profile_limit_reached'))
    heading.current?.focus()
    setCycle({ delay: 0 })
  }, [])

  const cancelAdding = useCallback(() => {
    focusAdd.current = true
    setAdding(false)
  }, [])

  if (!active) return null

  const profiles = list?.profiles ?? []
  const atLimit = list !== null && profiles.length >= list.maxProfiles
  const showForm = list !== null && !atLimit && (adding || !profiles.length)

  return (
    <div className="online-profiles">
      <h3 id={`${id}-title`} ref={heading} tabIndex={-1}>Saved profiles</h3>
      <p className="online-hint">
        Save a provider URL with the categories you want. Refresh downloads the latest playlist on the server and
        rebuilds your filtered playlist, so the newest version is ready to download at any time.
      </p>
      {list && (
        <p className="online-profile-meta">
          {formatCount(profiles.length)} of {plural(list.maxProfiles, 'profile', 'profiles')} used.
        </p>
      )}
      <p className="online-status online-profiles-status" role="status" aria-atomic="true">
        {!list && !problem ? 'Loading saved profiles...' : ''}
      </p>
      {problem && (
        <div className="online-profiles-problem">
          <p role="alert" className="online-error">{problem}</p>
          <div className="online-actions">
            <button
              type="button"
              className="btn-small"
              onClick={() => {
                setProblem('')
                reload()
              }}
            >
              Check profiles again
            </button>
          </div>
        </div>
      )}

      {profiles.length > 0 && (
        <ul className="online-profile-list" aria-labelledby={`${id}-title`}>
          {profiles.map(profile => (
            <ProfileItem
              key={profile.id}
              profile={profile}
              maxBytes={list?.maxBytes ?? null}
              config={config}
              auth={auth}
              onChange={update}
              onRemoved={remove}
              onStale={reload}
              onAccessError={onAccessError}
              takeFocus={takeFocus}
            />
          ))}
        </ul>
      )}

      {list && atLimit && (
        <p className="online-profile-meta">
          You have saved the maximum of {plural(list.maxProfiles, 'profile', 'profiles')}. Delete one to add another.
        </p>
      )}
      {showForm && (
        <ProfileCreateForm
          config={config}
          auth={auth}
          autoFocus={adding}
          onCreated={created}
          onCancel={profiles.length ? cancelAdding : null}
          onLimit={limitReached}
          onAccessError={onAccessError}
        />
      )}
      {list && !atLimit && !showForm && (
        <div className="online-actions">
          <button
            ref={addButton}
            type="button"
            className="btn-small"
            onClick={() => {
              setNotice('')
              setAdding(true)
            }}
          >
            Add profile
          </button>
        </div>
      )}

      <p className="online-status online-profiles-notice" role="status" aria-atomic="true">{notice}</p>
    </div>
  )
}

interface ProfileCreateFormProps {
  config: OnlineConfiguration
  auth: OnlineAuth
  autoFocus: boolean
  onCreated: (profile: OnlineProfile) => void
  onCancel: (() => void) | null
  onLimit: () => void
  onAccessError: (error: unknown) => void
}

function ProfileCreateForm({ config, auth, autoFocus, onCreated, onCancel, onLimit, onAccessError }: ProfileCreateFormProps) {
  const id = useId()
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [nameError, setNameError] = useState('')
  const [urlError, setUrlError] = useState('')
  const [formError, setFormError] = useState('')
  const [withFavourites, setWithFavourites] = useState(false)
  const [saving, setSaving] = useState(false)
  // Read once when the form opens, so the offer matches what the user sees.
  const [favourites] = useState(() => loadFavourites())
  const action = useRef<AbortController | null>(null)
  const nameInput = useRef<HTMLInputElement>(null)
  const urlInput = useRef<HTMLInputElement>(null)

  useEffect(() => () => action.current?.abort(), [])

  const favouriteCount = favourites.error ? 0 : favourites.categories.length
  const offerFavourites = favouriteCount > 0 && favouriteCount <= MAX_PROFILE_CATEGORIES
  const startWithFavourites = offerFavourites && withFavourites

  const submit = async () => {
    if (saving) return
    const invalidName = checkProfileName(name)
    const invalidUrl = checkSourceUrl(url)
    setNameError(invalidName)
    setUrlError(invalidUrl)
    setFormError('')
    if (invalidName) {
      nameInput.current?.focus()
      return
    }
    if (invalidUrl) {
      urlInput.current?.focus()
      return
    }

    action.current?.abort()
    const controller = new AbortController()
    action.current = controller
    setSaving(true)
    try {
      const credentials = await auth.acquireToken()
      if (controller.signal.aborted) return
      const request: NewOnlineProfile = startWithFavourites
        ? { name: name.trim(), url: url.trim(), categories: favourites.categories }
        : { name: name.trim(), url: url.trim() }
      const profile = await createProfile(config, credentials.accessToken, request, controller.signal)
      if (controller.signal.aborted) return
      onCreated(profile)
    } catch (error) {
      if (controller.signal.aborted) return
      const code = error instanceof OnlineProfileError ? error.code : ''
      if (isAccessError(error)) {
        onAccessError(error)
      } else if (code === 'invalid_url' || code === 'blocked_destination') {
        setUrlError(describeError(error))
        urlInput.current?.focus()
      } else if (code === 'invalid_name') {
        setNameError(describeError(error))
        nameInput.current?.focus()
      } else if (code === 'profile_limit_reached') {
        onLimit()
      } else {
        setFormError(describeError(error))
      }
    } finally {
      if (action.current === controller) {
        action.current = null
        setSaving(false)
      }
    }
  }

  return (
    <form
      className="online-profile-form"
      noValidate
      aria-labelledby={`${id}-title`}
      onSubmit={event => {
        event.preventDefault()
        void submit()
      }}
    >
      <h4 id={`${id}-title`}>New profile</h4>
      <label htmlFor={`${id}-name`}>Profile name</label>
      <input
        ref={nameInput}
        id={`${id}-name`}
        className="online-profile-input"
        type="text"
        autoComplete="off"
        autoFocus={autoFocus}
        maxLength={MAX_PROFILE_NAME_LENGTH}
        required
        placeholder="Living room TV"
        value={name}
        aria-invalid={nameError ? true : undefined}
        aria-describedby={nameError ? `${id}-name-error` : undefined}
        onChange={event => {
          setName(event.target.value)
          setNameError('')
        }}
      />
      {nameError && <p id={`${id}-name-error`} role="alert" className="online-error">{nameError}</p>}

      <label htmlFor={`${id}-url`}>Provider playlist URL</label>
      <input
        ref={urlInput}
        id={`${id}-url`}
        className="online-profile-input"
        type="url"
        inputMode="url"
        autoComplete="off"
        autoCapitalize="off"
        autoCorrect="off"
        spellCheck={false}
        required
        placeholder="http://provider.example:8080/get.php?..."
        value={url}
        aria-invalid={urlError ? true : undefined}
        aria-describedby={urlError ? `${id}-url-help ${id}-url-error` : `${id}-url-help`}
        onChange={event => {
          setUrl(event.target.value)
          setUrlError('')
        }}
      />
      <p id={`${id}-url-help`} className="online-profile-meta">
        The URL, including any credentials in it, is stored privately on the server with this profile so it can be
        refreshed. It is never shown again or saved in this browser, and it is kept until you delete the profile or
        change its URL.
      </p>
      {urlError && <p id={`${id}-url-error`} role="alert" className="online-error">{urlError}</p>}

      {offerFavourites && (
        <label className="online-profile-check">
          <input
            type="checkbox"
            checked={withFavourites}
            onChange={event => setWithFavourites(event.target.checked)}
          />
          <span>Start with my saved favourites ({plural(favouriteCount, 'category', 'categories')})</span>
        </label>
      )}
      <p className="online-profile-meta">
        Saving starts the first refresh on the server.{' '}
        {startWithFavourites
          ? 'It builds your filtered playlist from the favourites that are in the provider playlist.'
          : 'When it finishes, choose the categories to include.'}
      </p>
      {formError && <p role="alert" className="online-error">{formError}</p>}
      <div className="online-actions">
        <button type="submit" className="btn-small" disabled={saving}>
          {saving ? 'Saving profile...' : 'Save profile'}
        </button>
        {onCancel && (
          <button type="button" className="btn-small" disabled={saving} onClick={onCancel}>
            Cancel
          </button>
        )}
      </div>
    </form>
  )
}

interface ProfileItemProps {
  profile: OnlineProfile
  maxBytes: number | null
  config: OnlineConfiguration
  auth: OnlineAuth
  onChange: (profile: OnlineProfile) => void
  onRemoved: (profileId: string, message: string) => void
  onStale: () => void
  onAccessError: (error: unknown) => void
  takeFocus: (profileId: string) => boolean
}

function ProfileItem({
  profile,
  maxBytes,
  config,
  auth,
  onChange,
  onRemoved,
  onStale,
  onAccessError,
  takeFocus,
}: ProfileItemProps) {
  const id = useId()
  const profileId = profile.id
  const [panel, setPanel] = useState<Panel | null>(null)
  const [busy, setBusy] = useState<Action | null>(null)
  const [error, setError] = useState<ItemError | null>(null)
  const [notice, setNotice] = useState('')
  const [editorKey, setEditorKey] = useState(0)
  const action = useRef<AbortController | null>(null)
  const title = useRef<HTMLHeadingElement>(null)
  const panelTitle = useRef<HTMLHeadingElement>(null)
  const categoriesToggle = useRef<HTMLButtonElement>(null)
  const renameToggle = useRef<HTMLButtonElement>(null)
  const sourceToggle = useRef<HTMLButtonElement>(null)
  const deleteToggle = useRef<HTMLButtonElement>(null)
  // The panel whose toggle gets focus back once that panel closes.
  const restoreFocus = useRef<Panel | null>(null)

  useEffect(() => {
    if (takeFocus(profileId)) title.current?.focus()
  }, [takeFocus, profileId])

  useEffect(() => () => action.current?.abort(), [])

  useEffect(() => {
    if (panel === 'categories') panelTitle.current?.focus()
  }, [panel])

  // Waits until no action runs, because the toggles are disabled meanwhile and cannot take focus.
  useEffect(() => {
    const target = restoreFocus.current
    if (panel !== null || busy !== null || target === null) return
    restoreFocus.current = null
    const toggles = { categories: categoriesToggle, rename: renameToggle, source: sourceToggle, delete: deleteToggle }
    toggles[target].current?.focus()
  }, [panel, busy])

  const onMissing = useCallback(
    () => onRemoved(profileId, describeProfileFailure('profile_not_found')),
    [onRemoved, profileId],
  )

  const begin = (kind: Action) => {
    action.current?.abort()
    const controller = new AbortController()
    action.current = controller
    setBusy(kind)
    setError(null)
    setNotice('')
    return controller
  }

  const finish = (controller: AbortController) => {
    if (action.current !== controller) return
    action.current = null
    setBusy(null)
  }

  const report = (origin: Action, failure: unknown) => {
    if (isAccessError(failure)) {
      onAccessError(failure)
    } else if (failure instanceof OnlineProfileError && failure.kind === 'not-found') {
      onRemoved(profileId, failure.message)
    } else {
      const code = failure instanceof OnlineProfileError ? failure.code : 'token_unavailable'
      setError({ origin, code, message: describeError(failure) })
      if (STALE_CODES.has(code)) onStale()
    }
  }

  const run = async (kind: Action, work: (accessToken: string, signal: AbortSignal) => Promise<void>) => {
    if (busy) return
    const controller = begin(kind)
    try {
      const credentials = await auth.acquireToken()
      if (controller.signal.aborted) return
      await work(credentials.accessToken, controller.signal)
    } catch (failure) {
      if (!controller.signal.aborted) report(kind, failure)
    } finally {
      finish(controller)
    }
  }

  const closePanel = (kind: Panel) => {
    restoreFocus.current = kind
    setPanel(null)
    setError(null)
  }

  const togglePanel = (kind: Panel) => {
    if (panel === kind) {
      closePanel(kind)
      return
    }
    setPanel(kind)
    setError(null)
    setNotice('')
  }

  const clearError = () => setError(null)

  const reloadEditor = () => {
    setEditorKey(key => key + 1)
    setError(null)
    onStale()
  }

  const refresh = () => run('refresh', async (accessToken, signal) => {
    const updated = await refreshProfile(config, accessToken, profileId, signal)
    if (signal.aborted) return
    onChange(updated)
  })

  const download = () => run('download', async (accessToken, signal) => {
    const playlist = await downloadProfileOutput(config, accessToken, profileId, signal)
    if (signal.aborted) return
    const fileName = profileFileName(profile.name)
    const href = URL.createObjectURL(playlist)
    const link = document.createElement('a')
    link.href = href
    link.download = fileName
    link.hidden = true
    document.body.append(link)
    link.click()
    link.remove()
    window.setTimeout(() => URL.revokeObjectURL(href), REVOKE_DOWNLOAD_MS)
    setNotice(`Downloaded ${fileName} (${formatBytes(playlist.size)}). Upload this file to your TV app.`)
  })

  const rename = (name: string) => run('rename', async (accessToken, signal) => {
    const updated = await renameProfile(config, accessToken, profileId, name, signal)
    if (signal.aborted) return
    onChange(updated)
    closePanel('rename')
    setNotice(`Profile renamed to "${updated.name}".`)
  })

  const changeSource = (url: string) => run('source', async (accessToken, signal) => {
    const updated = await changeProfileSource(config, accessToken, profileId, url, signal)
    if (signal.aborted) return
    onChange(updated)
    closePanel('source')
    setNotice('Provider URL changed. The profile is refreshing from the new URL.')
  })

  const saveCategories = (names: string[]) => run('categories', async (accessToken, signal) => {
    const updated = await saveProfileCategories(config, accessToken, profileId, names, signal)
    if (signal.aborted) return
    onChange(updated)
    closePanel('categories')
    setNotice(
      `Categories saved (${plural(names.length, 'category', 'categories')}). ` +
        'The server is rebuilding your filtered playlist.',
    )
  })

  const remove = () => run('delete', async (accessToken, signal) => {
    await deleteProfile(config, accessToken, profileId, signal)
    if (signal.aborted) return
    onRemoved(
      profileId,
      `Profile "${profile.name}" was deleted. Its stored URL and playlists were removed from the server.`,
    )
  })

  const refreshState = profile.refresh
  const activeRefresh = isRefreshActive(profile)
  const failed = refreshState?.status === 'failed'
  const downloading = refreshState?.status === 'running' && refreshState.stage === 'downloading' ? refreshState : null
  const knownTotal = downloading?.totalBytes ? downloading.totalBytes : null
  const transfer = describeTransfer(refreshState)
  const output = profile.output
  const missingCount = output?.missingCategoryCount ?? 0
  const missingNames = output?.missingCategories ?? []
  const isBusy = busy !== null
  const itemError = error && (error.origin === 'refresh' || error.origin === 'download') ? error.message : ''
  const panelError = error && error.origin === panel ? error.message : ''
  const panelId = `${id}-panel`
  const toggleState = (kind: Panel) => ({
    'aria-expanded': panel === kind,
    'aria-controls': panel === kind ? panelId : undefined,
  })

  return (
    <li className="online-profile">
      <h4 id={`${id}-title`} ref={title} tabIndex={-1}>{profile.name}</h4>
      {profile.source && <p className="online-profile-meta">Source: <strong>{profile.source}</strong></p>}
      <p
        className={`online-status online-profile-status${failed ? ' online-profile-failed' : ''}`}
        role="status"
        aria-atomic="true"
      >
        {describeRefresh(profile, maxBytes)}
      </p>
      {activeRefresh && (
        <progress
          className="online-profile-progress"
          aria-label={`Refresh progress for ${profile.name}`}
          value={downloading && knownTotal ? Math.min(downloading.bytesDownloaded, knownTotal) : undefined}
          max={knownTotal ?? undefined}
        />
      )}
      {transfer && <p className="online-profile-meta">{transfer}</p>}
      <p className="online-profile-meta">{describeOutput(output)}</p>
      <p className="online-profile-meta">{describeSelection(profile)}</p>
      {missingCount > 0 && (
        <details className="online-profile-missing">
          <summary>
            {plural(missingCount, 'selected category was', 'selected categories were')} missing from the provider
            playlist at the last refresh
          </summary>
          <p className="online-profile-meta">
            They stay selected and are included again automatically when the provider lists them.
          </p>
          {missingNames.length > 0 && (
            <ul>
              {missingNames.map(name => <li key={name}>{name}</li>)}
            </ul>
          )}
          {missingCount > missingNames.length && (
            <p className="online-profile-meta">...and {formatCount(missingCount - missingNames.length)} more.</p>
          )}
        </details>
      )}

      <div className="online-actions" role="group" aria-label={`Actions for ${profile.name}`}>
        <button type="button" className="btn-small" disabled={isBusy || activeRefresh} onClick={() => void refresh()}>
          {busy === 'refresh' ? 'Starting refresh...' : activeRefresh ? 'Refreshing...' : 'Refresh now'}
        </button>
        <button type="button" className="btn-small" disabled={isBusy || !output} onClick={() => void download()}>
          {busy === 'download' ? 'Downloading...' : 'Download latest'}
        </button>
        <button
          ref={categoriesToggle}
          type="button"
          className="btn-small"
          disabled={isBusy || !profile.index}
          {...toggleState('categories')}
          onClick={() => togglePanel('categories')}
        >
          Edit categories
        </button>
        <button
          ref={renameToggle}
          type="button"
          className="btn-small"
          disabled={isBusy}
          {...toggleState('rename')}
          onClick={() => togglePanel('rename')}
        >
          Rename
        </button>
        <button
          ref={sourceToggle}
          type="button"
          className="btn-small"
          disabled={isBusy}
          {...toggleState('source')}
          onClick={() => togglePanel('source')}
        >
          Change URL
        </button>
        <button
          ref={deleteToggle}
          type="button"
          className="btn-small"
          disabled={isBusy}
          {...toggleState('delete')}
          onClick={() => togglePanel('delete')}
        >
          Delete
        </button>
      </div>

      {panel && (
        <div
          id={panelId}
          className="online-profile-panel"
          role="group"
          aria-labelledby={`${id}-panel-title`}
          onKeyDown={event => {
            if (event.key !== 'Escape' || isBusy) return
            event.stopPropagation()
            closePanel(panel)
          }}
        >
          <h5 id={`${id}-panel-title`} ref={panelTitle} tabIndex={-1}>{describePanel(panel, profile.name)}</h5>
          {panel === 'categories' && (
            <ProfileCategoryEditor
              key={editorKey}
              profile={profile}
              config={config}
              auth={auth}
              busy={isBusy}
              saving={busy === 'categories'}
              error={panelError}
              invalidSelection={error?.origin === 'categories' && error.code === 'invalid_selection'}
              onSave={names => void saveCategories(names)}
              onCancel={() => closePanel('categories')}
              onReload={reloadEditor}
              onAccessError={onAccessError}
              onMissing={onMissing}
            />
          )}
          {panel === 'rename' && (
            <ProfileRenameForm
              current={profile.name}
              busy={isBusy}
              saving={busy === 'rename'}
              error={panelError}
              onSave={name => void rename(name)}
              onCancel={() => closePanel('rename')}
              onEdit={clearError}
            />
          )}
          {panel === 'source' && (
            <ProfileSourceForm
              busy={isBusy}
              saving={busy === 'source'}
              locked={activeRefresh}
              error={panelError}
              onSave={url => void changeSource(url)}
              onCancel={() => closePanel('source')}
              onEdit={clearError}
            />
          )}
          {panel === 'delete' && (
            <>
              <p className="online-profile-meta">
                This deletes the profile, its stored provider URL and its filtered playlists from the server. It cannot
                be undone.
              </p>
              {panelError && <p role="alert" className="online-error">{panelError}</p>}
              <div className="online-actions">
                <button
                  type="button"
                  className="btn-small online-profile-danger"
                  disabled={isBusy}
                  onClick={() => void remove()}
                >
                  {busy === 'delete' ? 'Deleting profile...' : 'Delete profile'}
                </button>
                <button
                  type="button"
                  className="btn-small"
                  autoFocus
                  disabled={isBusy}
                  onClick={() => closePanel('delete')}
                >
                  Cancel
                </button>
              </div>
            </>
          )}
        </div>
      )}

      <ProfileFeedLink profile={profile} config={config} auth={auth} onAccessError={onAccessError} onMissing={onMissing} />
      {itemError && <p role="alert" className="online-error">{itemError}</p>}
      <p className="online-status online-profile-notice" role="status" aria-atomic="true">{notice}</p>
    </li>
  )
}

interface ProfileRenameFormProps {
  current: string
  busy: boolean
  saving: boolean
  error: string
  onSave: (name: string) => void
  onCancel: () => void
  onEdit: () => void
}

function ProfileRenameForm({ current, busy, saving, error, onSave, onCancel, onEdit }: ProfileRenameFormProps) {
  const id = useId()
  const [name, setName] = useState(current)
  const [problem, setProblem] = useState('')
  const input = useRef<HTMLInputElement>(null)
  const message = problem || error

  useEffect(() => {
    if (error) input.current?.focus()
  }, [error])

  return (
    <form
      className="online-profile-form"
      noValidate
      onSubmit={event => {
        event.preventDefault()
        if (busy) return
        const invalid = checkProfileName(name)
        setProblem(invalid)
        if (invalid) {
          input.current?.focus()
          return
        }
        if (name.trim() === current) {
          onCancel()
          return
        }
        onSave(name.trim())
      }}
    >
      <label htmlFor={`${id}-name`}>New name</label>
      <input
        ref={input}
        id={`${id}-name`}
        className="online-profile-input"
        type="text"
        autoComplete="off"
        autoFocus
        maxLength={MAX_PROFILE_NAME_LENGTH}
        required
        value={name}
        aria-invalid={message ? true : undefined}
        aria-describedby={message ? `${id}-error` : undefined}
        onChange={event => {
          setName(event.target.value)
          setProblem('')
          onEdit()
        }}
      />
      {message && <p id={`${id}-error`} role="alert" className="online-error">{message}</p>}
      <div className="online-actions">
        <button type="submit" className="btn-small" disabled={busy}>
          {saving ? 'Saving name...' : 'Save name'}
        </button>
        <button type="button" className="btn-small" disabled={busy} onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  )
}

interface ProfileSourceFormProps {
  busy: boolean
  saving: boolean
  /** True while a refresh is queued or running; the API only accepts a new URL between refreshes. */
  locked: boolean
  error: string
  onSave: (url: string) => void
  onCancel: () => void
  onEdit: () => void
}

function ProfileSourceForm({ busy, saving, locked, error, onSave, onCancel, onEdit }: ProfileSourceFormProps) {
  const id = useId()
  // Never prefilled: the stored URL is not available to the browser.
  const [url, setUrl] = useState('')
  const [problem, setProblem] = useState('')
  const input = useRef<HTMLInputElement>(null)
  const message = problem || error

  useEffect(() => {
    if (error) input.current?.focus()
  }, [error])

  return (
    <form
      className="online-profile-form"
      noValidate
      onSubmit={event => {
        event.preventDefault()
        if (busy || locked) return
        const invalid = checkSourceUrl(url)
        setProblem(invalid)
        if (invalid) {
          input.current?.focus()
          return
        }
        onSave(url.trim())
      }}
    >
      <label htmlFor={`${id}-url`}>New provider playlist URL</label>
      <input
        ref={input}
        id={`${id}-url`}
        className="online-profile-input"
        type="url"
        inputMode="url"
        autoComplete="off"
        autoCapitalize="off"
        autoCorrect="off"
        spellCheck={false}
        autoFocus
        required
        placeholder="http://provider.example:8080/get.php?..."
        value={url}
        aria-invalid={message ? true : undefined}
        aria-describedby={message ? `${id}-help ${id}-error` : `${id}-help`}
        onChange={event => {
          setUrl(event.target.value)
          setProblem('')
          onEdit()
        }}
      />
      <p id={`${id}-help`} className="online-profile-meta">
        The current URL is never shown. Saving replaces it on the server and refreshes the profile from the new URL.
        Your selected categories are kept.
      </p>
      {message && <p id={`${id}-error`} role="alert" className="online-error">{message}</p>}
      {locked && <p className="online-profile-meta">You can save after the current refresh finishes.</p>}
      <div className="online-actions">
        <button type="submit" className="btn-small" disabled={busy || locked}>
          {saving ? 'Saving URL...' : 'Save URL'}
        </button>
        <button type="button" className="btn-small" disabled={busy} onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  )
}

type EditorEntry = ProfileCategory & {
  /** Selected before, but missing from the latest playlist. It can stay selected until the provider lists it again. */
  kept: boolean
}

function buildEntries(loaded: ProfileCategories | null): EditorEntry[] {
  if (!loaded) return []
  const saved = new Set(loaded.selected)
  const indexed = new Set(loaded.categories.map(category => category.name))
  const absent = loaded.selected.filter(name => !indexed.has(name)).map(name => ({ name, count: 0, kept: true }))
  return [
    ...absent,
    ...loaded.categories.map(category => ({ ...category, kept: category.count === 0 && saved.has(category.name) })),
  ]
}

const isSelectable = (entry: EditorEntry) => entry.count > 0 || entry.kept

interface ProfileCategoryEditorProps {
  profile: OnlineProfile
  config: OnlineConfiguration
  auth: OnlineAuth
  busy: boolean
  saving: boolean
  error: string
  invalidSelection: boolean
  onSave: (names: string[]) => void
  onCancel: () => void
  onReload: () => void
  onAccessError: (error: unknown) => void
  onMissing: () => void
}

function ProfileCategoryEditor({
  profile,
  config,
  auth,
  busy,
  saving,
  error,
  invalidSelection,
  onSave,
  onCancel,
  onReload,
  onAccessError,
  onMissing,
}: ProfileCategoryEditorProps) {
  const id = useId()
  const profileId = profile.id
  const [loaded, setLoaded] = useState<ProfileCategories | null>(null)
  const [loadError, setLoadError] = useState('')
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set())
  const [search, setSearch] = useState('')
  const [note, setNote] = useState<Note | null>(null)
  const entries = useMemo(() => buildEntries(loaded), [loaded])

  useEffect(() => {
    const controller = new AbortController()

    const load = async () => {
      try {
        const credentials = await auth.acquireToken()
        if (controller.signal.aborted) return
        const result = await getProfileCategories(config, credentials.accessToken, profileId, controller.signal)
        if (controller.signal.aborted) return
        setLoaded(result)
        setSelected(new Set(result.selected))
      } catch (failure) {
        if (controller.signal.aborted) return
        if (isAccessError(failure)) {
          onAccessError(failure)
        } else if (failure instanceof OnlineProfileError && failure.kind === 'not-found') {
          onMissing()
        } else {
          setLoadError(describeError(failure))
        }
      }
    }

    void load()
    return () => controller.abort()
  }, [profileId, auth, config, onAccessError, onMissing])

  const query = search.toLowerCase()
  const visible = query ? entries.filter(entry => entry.name.toLowerCase().includes(query)) : entries
  const selectableShown = visible.filter(isSelectable)
  const shownSelected = visible.some(entry => selected.has(entry.name))
  const chosen = entries.filter(entry => selected.has(entry.name))
  const chosenChannels = chosen.reduce((total, entry) => total + entry.count, 0)
  const activeRefresh = isRefreshActive(profile)
  const tooMany = chosen.length > MAX_PROFILE_CATEGORIES
  const loadedAt = loaded?.indexedAt ?? null
  const latestAt = profile.index?.indexedAt ?? null
  const outdated = invalidSelection || (loadedAt !== null && latestAt !== null && loadedAt !== latestAt)
  const blocked = activeRefresh
    ? 'You can save after the current refresh finishes.'
    : !chosen.length
      ? 'Select at least one category to save.'
      : tooMany
        ? `Select at most ${formatCount(MAX_PROFILE_CATEGORIES)} categories.`
        : ''

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

  const selectShown = () => {
    setSelected(previous => new Set([...previous, ...selectableShown.map(entry => entry.name)]))
    setNote(null)
  }

  const clearShown = () => {
    const shown = new Set(visible.map(entry => entry.name))
    setSelected(previous => new Set([...previous].filter(name => !shown.has(name))))
    setNote(null)
  }

  const applyFavourites = () => {
    const saved = loadFavourites()
    if (saved.error) {
      setNote({ text: saved.error, error: true })
      return
    }
    if (!saved.categories.length) {
      setNote({
        text: 'No favourites are saved in this browser yet. Save favourites from a local playlist first.',
        error: false,
      })
      return
    }
    const available = entries.filter(entry => entry.count > 0).map(entry => entry.name)
    const { matched, missing } = matchFavourites(saved.categories, available)
    if (!matched.length) {
      setNote({
        text: 'None of your favourites are in the latest playlist. Your selection is unchanged.',
        error: false,
      })
      return
    }
    setSelected(new Set(matched))
    setNote({
      text: `${plural(matched.length, 'favourite', 'favourites')} selected` +
        (missing.length ? `; ${formatCount(missing.length)} not in the latest playlist (kept in your favourites).` : '.'),
      error: false,
    })
  }

  const save = () => {
    if (busy || blocked) return
    onSave(chosen.map(entry => entry.name))
  }

  return (
    <div className="online-profile-editor">
      <p className="online-status online-profile-loading" role="status" aria-atomic="true">
        {!loaded && !loadError ? 'Loading categories...' : ''}
      </p>
      {loadError && (
        <>
          <p role="alert" className="online-error">{loadError}</p>
          <div className="online-actions">
            <button type="button" className="btn-small" onClick={onReload}>
              Try again
            </button>
            <button type="button" className="btn-small" onClick={onCancel}>
              Cancel
            </button>
          </div>
        </>
      )}
      {loaded && (
        <>
          {outdated && (
            <div className="online-profile-outdated">
              <p className="online-profile-meta">
                {invalidSelection
                  ? 'The provider playlist changed since these categories were loaded.'
                  : 'The profile was refreshed since these categories were loaded.'}{' '}
                Reload them to see the latest list. Your unsaved changes will be lost.
              </p>
              <div className="online-actions">
                <button type="button" className="btn-small" disabled={busy} onClick={onReload}>
                  Reload categories
                </button>
              </div>
            </div>
          )}

          <div className="category-toolbar">
            <div className="search-box">
              <span className="search-icon" aria-hidden="true">🔍</span>
              <input
                type="text"
                aria-label={`Search the categories of ${profile.name}`}
                placeholder="Search categories..."
                autoComplete="off"
                value={search}
                onChange={event => setSearch(event.target.value)}
              />
              {search && (
                <button
                  type="button"
                  className="search-clear"
                  aria-label="Clear category search"
                  onClick={() => setSearch('')}
                >
                  ✕
                </button>
              )}
            </div>
            <div className="select-actions">
              <button type="button" className="btn-small" disabled={!selectableShown.length} onClick={selectShown}>
                {query ? 'Select shown' : 'Select all'}
              </button>
              <button type="button" className="btn-small" disabled={!shownSelected} onClick={clearShown}>
                {query ? 'Clear shown' : 'Select none'}
              </button>
              <button
                type="button"
                className="btn-small"
                disabled={busy}
                aria-describedby={`${id}-favourites`}
                onClick={applyFavourites}
              >
                Apply favourites
              </button>
            </div>
          </div>
          <p id={`${id}-favourites`} className="online-profile-meta">
            Applying favourites replaces the selection with your favourites saved in this browser.
          </p>
          <p className="favourites-status" role="status" aria-atomic="true">{note && !note.error ? note.text : ''}</p>
          {note?.error && <p className="message favourites-error" role="alert">{note.text}</p>}

          <div className="categories" role="group" aria-label={`Categories of ${profile.name}`}>
            {!visible.length ? (
              <div className="empty-state">
                <p>{entries.length ? `No categories match "${search}"` : 'The latest playlist has no categories.'}</p>
              </div>
            ) : (
              visible.map(entry => {
                const checked = selected.has(entry.name)
                const selectable = isSelectable(entry)
                return (
                  <label
                    key={entry.name}
                    className={`category-item${checked ? ' selected' : ''}${selectable ? '' : ' online-profile-empty'}`}
                  >
                    <input
                      type="checkbox"
                      checked={checked}
                      disabled={!selectable}
                      aria-label={entry.kept
                        ? `${entry.name} (missing from the latest playlist)`
                        : `${entry.name} (${plural(entry.count, 'channel', 'channels')})`}
                      onChange={() => toggle(entry.name)}
                    />
                    <span className="category-name">{entry.name}</span>
                    <span className={`online-profile-count${entry.kept ? ' online-profile-kept' : ''}`} aria-hidden="true">
                      {entry.kept ? 'missing' : formatCount(entry.count)}
                    </span>
                  </label>
                )
              })
            )}
          </div>

          <p className="online-profile-meta">
            {plural(chosen.length, 'category', 'categories')} selected with {plural(chosenChannels, 'channel', 'channels')}.
          </p>
          {error && <p role="alert" className="online-error">{error}</p>}
          <div className="online-actions">
            <button type="button" className="btn-small" disabled={busy || blocked !== ''} onClick={save}>
              {saving ? 'Saving categories...' : 'Save categories'}
            </button>
            <button type="button" className="btn-small" disabled={busy} onClick={onCancel}>
              Cancel
            </button>
          </div>
          {blocked && <p className="hint">{blocked}</p>}
        </>
      )}
    </div>
  )
}
