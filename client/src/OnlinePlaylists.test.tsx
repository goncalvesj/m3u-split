import { StrictMode } from 'react'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import { FAVOURITES_STORAGE_KEY, loadFavourites } from './favourites'
import type { OnlineAccount, OnlineAuth, OnlineCredentials } from './online-auth'

const { createAuth } = vi.hoisted(() => ({ createAuth: vi.fn<() => Promise<OnlineAuth>>() }))
vi.mock('./online-auth', () => ({ createOnlineAuth: createAuth }))

const tenantId = '11111111-1111-4111-8111-111111111111'
const account: OnlineAccount = { id: 'test-account', label: 'Allowed person' }
const credentials = (): OnlineCredentials => ({
  account,
  accessToken: 'api-access-token',
  expiresAt: Date.now() + 60_000,
})
const body = () => ({
  tenantId,
  objectId: '44444444-4444-4444-8444-444444444444',
  expiresAt: new Date(Date.now() + 60_000).toISOString(),
})
const environment = {
  VITE_ENTRA_TENANT_ID: tenantId,
  VITE_ENTRA_SPA_CLIENT_ID: '22222222-2222-4222-8222-222222222222',
  VITE_ENTRA_API_SCOPE: 'api://33333333-3333-4333-8333-333333333333/access_as_user',
  VITE_ONLINE_API_BASE_URL: 'https://function.example.invalid/api/',
}
const sports = '#EXTINF:-1 group-title="Sports",Sports channel\nhttps://example.invalid/sports\n'
const news = '#EXTINF:-1 group-title="News",News channel\nhttps://example.invalid/news\n'
const online = () => within(screen.getByRole('region', { name: 'Online playlists' }))
const button = (name: string | RegExp) => screen.getByRole<HTMLButtonElement>('button', { name })
const checkbox = (name: string) => screen.getByRole<HTMLInputElement>('checkbox', { name })
const renderApp = () => render(<StrictMode><App /></StrictMode>)
const fetchMock = vi.fn<typeof fetch>()
let auth: OnlineAuth
let cached: OnlineAccount | null

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

async function loadLocalPlaylist() {
  const content = `#EXTM3U\n${sports}${news}`
  const file = new File([content], 'loaded-playlist.m3u', { type: 'audio/x-mpegurl' })
  Object.defineProperty(file, 'text', { value: async () => content })
  fireEvent.change(screen.getByLabelText('Choose M3U file'), { target: { files: [file] } })
  await screen.findByRole('heading', { name: 'Select Categories' })
}

function captureDownloads() {
  const createObjectURL = vi.fn<(blob: Blob) => string>(() => 'blob:local-download')
  vi.stubGlobal('URL', class extends URL { static createObjectURL = createObjectURL })
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
  return createObjectURL
}

const blobText = (blob: Blob) => new Promise<string>((resolve, reject) => {
  const reader = new FileReader()
  reader.onload = () => resolve(String(reader.result))
  reader.onerror = () => reject(reader.error)
  reader.readAsText(blob)
})

beforeEach(() => {
  vi.resetAllMocks()
  window.localStorage.clear()
  Object.entries(environment).forEach(([key, value]) => vi.stubEnv(key, value))
  cached = null
  auth = {
    getAccount: vi.fn(() => cached),
    signIn: vi.fn(async () => { cached = account; return credentials() }),
    acquireToken: vi.fn(async () => credentials()),
    signOut: vi.fn(async () => { cached = null }),
  }
  createAuth.mockResolvedValue(auth)
  fetchMock.mockImplementation(async () => Response.json(body()))
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  cleanup()
  vi.unstubAllEnvs()
})

describe('optional online sign-in boundary', () => {
  it('does not initialize authentication or contact a server in the guest-only build', async () => {
    Object.keys(environment).forEach(key => vi.stubEnv(key, ''))
    const user = userEvent.setup()
    const downloads = captureDownloads()
    renderApp()

    expect(online().getByText(/Online sign-in is not configured/)).toBeDefined()
    expect(online().queryByRole('button')).toBeNull()
    await loadLocalPlaylist()
    await user.click(checkbox('Sports'))
    await user.click(button('Save selection as favourites'))
    await user.type(screen.getByRole('textbox', { name: 'Search categories' }), 'Sports')
    await user.click(button(/Download M3U File/))

    expect(await blobText(downloads.mock.calls[0][0])).toBe(sports)
    expect(loadFavourites().categories).toEqual(['Sports'])
    expect(createAuth).not.toHaveBeenCalled()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('fails closed for partial configuration without hiding local processing', async () => {
    vi.stubEnv('VITE_ENTRA_SPA_CLIENT_ID', '')
    renderApp()
    expect(online().getByRole('alert').textContent).toContain('application IDs')
    await loadLocalPlaylist()
    fireEvent.click(checkbox('News'))
    expect(button(/Download M3U File/).disabled).toBe(false)
    expect(createAuth).not.toHaveBeenCalled()
  })

  it('keeps local processing usable while authentication initializes and supports setup retry', async () => {
    const initialization = deferred<OnlineAuth>()
    createAuth.mockReturnValueOnce(initialization.promise)
    renderApp()
    expect(button('Sign in with Microsoft').disabled).toBe(true)
    await loadLocalPlaylist()
    fireEvent.click(checkbox('Sports'))
    expect(button(/Download M3U File/).disabled).toBe(false)
    await act(async () => initialization.reject(new Error('Cannot load MSAL')))
    expect(online().getByRole('alert').textContent).toContain('could not initialize')
    fireEvent.click(button('Retry sign-in setup'))
    await waitFor(() => expect(button('Sign in with Microsoft').disabled).toBe(false))
    expect(checkbox('Sports').checked).toBe(true)
    expect(createAuth).toHaveBeenCalledTimes(2)
  })

  it('does not mistake Microsoft sign-in for backend authorization', async () => {
    const pending = deferred<Response>()
    fetchMock.mockReturnValueOnce(pending.promise)
    const user = userEvent.setup()
    renderApp()
    await waitFor(() => expect(button('Sign in with Microsoft').disabled).toBe(false))
    await user.click(button('Sign in with Microsoft'))

    expect(online().getByText('Allowed person')).toBeDefined()
    expect(online().getByRole('status').textContent).toContain('Checking access')
    expect(online().queryByText('Online access verified by the API.')).toBeNull()
    expect(fetchMock).toHaveBeenCalledOnce()
    await act(async () => pending.resolve(new Response('', { status: 403 })))
    expect(online().getByRole('alert').textContent).toContain('not permitted')
    expect(online().queryByText(/Only the sign-in boundary is ready/)).toBeNull()
  })

  it('preserves the loaded file, exact selection, search and favourites through sign-in and sign-out', async () => {
    const user = userEvent.setup()
    const downloads = captureDownloads()
    window.localStorage.setItem('unrelated-setting', 'keep')
    renderApp()
    await loadLocalPlaylist()
    await user.click(checkbox('Sports'))
    await user.click(button('Save selection as favourites'))
    const saved = window.localStorage.getItem(FAVOURITES_STORAGE_KEY)
    const search = screen.getByRole<HTMLInputElement>('textbox', { name: 'Search categories' })
    await user.type(search, 'Sports')
    await user.click(button('Sign in with Microsoft'))
    await screen.findByText('Online access verified by the API.')

    expect(online().getByText(/Only the sign-in boundary is ready/)).toBeDefined()
    expect(online().queryByRole('textbox')).toBeNull()
    expect(online().queryByRole('button', { name: /import|save|refresh|publish|TV/i })).toBeNull()
    expect(screen.getByText('loaded-playlist.m3u')).toBeDefined()
    expect(search.value).toBe('Sports')
    expect(checkbox('Sports').checked).toBe(true)
    await user.click(button(/Download M3U File/))
    await user.click(button('Sign out'))
    await screen.findByText(/Signed out of this app/)

    expect(screen.getByText('loaded-playlist.m3u')).toBeDefined()
    expect(search.value).toBe('Sports')
    expect(checkbox('Sports').checked).toBe(true)
    expect(window.localStorage.getItem(FAVOURITES_STORAGE_KEY)).toBe(saved)
    expect(window.localStorage.getItem('unrelated-setting')).toBe('keep')
    await user.click(button('Clear search'))
    expect(checkbox('News').checked).toBe(false)
    await user.click(button(/Download M3U File/))
    expect(downloads).toHaveBeenCalledTimes(2)
    expect(await blobText(downloads.mock.calls[0][0])).toBe(sports)
    expect(await blobText(downloads.mock.calls[1][0])).toBe(sports)
    expect(auth.signOut).toHaveBeenCalledOnce()
    expect(fetchMock).toHaveBeenCalledOnce()
  })

  it.each([
    ['user_cancelled', 'cancelled'],
    ['popup_window_error', 'popup was blocked'],
    ['empty_window_error', 'popup was blocked'],
    ['timed_out', 'did not complete'],
    ['interaction_in_progress', 'already open'],
    ['consent_required', 'API permission'],
  ])('isolates authentication failure %s from the current local file', async (errorCode, message) => {
    vi.mocked(auth.signIn).mockRejectedValue({ errorCode })
    const user = userEvent.setup()
    renderApp()
    await loadLocalPlaylist()
    await user.click(checkbox('News'))
    await user.click(button('Save selection as favourites'))
    await user.click(button('Sign in with Microsoft'))

    expect(online().getByRole('alert').textContent).toContain(message)
    expect(checkbox('News').checked).toBe(true)
    expect(loadFavourites().categories).toEqual(['News'])
    expect(button(/Download M3U File/).disabled).toBe(false)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it.each([401, 403, 503])('leaves local download usable after the backend rejects access with %s', async status => {
    fetchMock.mockResolvedValue(new Response('', { status }))
    const user = userEvent.setup()
    const downloads = captureDownloads()
    renderApp()
    await loadLocalPlaylist()
    await user.click(checkbox('Sports'))
    await user.click(button('Sign in with Microsoft'))
    await waitFor(() => expect(online().getByRole('alert')).toBeDefined())

    expect(online().queryByText('Online access verified by the API.')).toBeNull()
    await user.click(button(/Download M3U File/))
    expect(await blobText(downloads.mock.calls[0][0])).toBe(sports)
  })

  it('can retry an unreachable backend without another popup or losing the local file', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Offline'))
    const user = userEvent.setup()
    renderApp()
    await loadLocalPlaylist()
    await user.click(checkbox('News'))
    await user.click(button('Sign in with Microsoft'))
    await waitFor(() => expect(online().getByRole('alert').textContent).toContain('Could not reach'))
    await user.click(button('Check online access'))
    await screen.findByText('Online access verified by the API.')

    expect(auth.signIn).toHaveBeenCalledOnce()
    expect(auth.acquireToken).toHaveBeenCalledOnce()
    expect(checkbox('News').checked).toBe(true)
    expect(screen.getByText('loaded-playlist.m3u')).toBeDefined()
  })

  it('verifies cached Microsoft accounts rather than restoring a cached authorization claim', async () => {
    cached = account
    fetchMock.mockResolvedValue(new Response('', { status: 403 }))
    renderApp()
    await waitFor(() => expect(online().getByRole('alert').textContent).toContain('not permitted'))
    expect(auth.acquireToken).toHaveBeenCalledOnce()
    expect(auth.signIn).not.toHaveBeenCalled()
    expect(createAuth).toHaveBeenCalledOnce()
  })

  it('requires an explicit new sign-in when silent renewal cannot complete', async () => {
    cached = account
    vi.mocked(auth.acquireToken).mockRejectedValue({ errorCode: 'interaction_required' })
    renderApp()
    await waitFor(() => expect(online().getByRole('alert').textContent).toContain('sign in again'))
    expect(auth.signIn).not.toHaveBeenCalled()
    expect(fetchMock).not.toHaveBeenCalled()
    expect(button('Sign in again / change account').disabled).toBe(false)
  })

  it('drops verified status at expiry after the app resumes, then renews through the API', async () => {
    const user = userEvent.setup()
    renderApp()
    await waitFor(() => expect(button('Sign in with Microsoft').disabled).toBe(false))
    await user.click(button('Sign in with Microsoft'))
    await screen.findByText('Online access verified by the API.')
    vi.spyOn(Date, 'now').mockReturnValue(Date.now() + 120_000)
    fireEvent.focus(window)

    expect(online().queryByText('Online access verified by the API.')).toBeNull()
    expect(online().getByRole('alert').textContent).toContain('expired')
    await user.click(button('Check online access'))
    await screen.findByText('Online access verified by the API.')
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('does not continue claiming verified online access after going offline', async () => {
    const user = userEvent.setup()
    renderApp()
    await waitFor(() => expect(button('Sign in with Microsoft').disabled).toBe(false))
    await user.click(button('Sign in with Microsoft'))
    await screen.findByText('Online access verified by the API.')
    fireEvent(window, new Event('offline'))
    expect(online().getByRole('alert').textContent).toContain('offline')
    expect(online().queryByText('Online access verified by the API.')).toBeNull()
  })

  it('clears verified status as sign-out starts and reports incomplete Microsoft sign-out honestly', async () => {
    const pending = deferred<void>()
    vi.mocked(auth.signOut).mockReturnValueOnce(pending.promise)
    const user = userEvent.setup()
    renderApp()
    await loadLocalPlaylist()
    await user.click(checkbox('News'))
    await user.click(button('Save selection as favourites'))
    await user.click(button('Sign in with Microsoft'))
    await screen.findByText('Online access verified by the API.')
    await user.click(button('Sign out'))

    expect(online().queryByText('Online access verified by the API.')).toBeNull()
    expect(online().getByRole('status').textContent).toContain('Signing out')
    expect(button(/Download M3U File/).disabled).toBe(false)
    cached = null
    await act(async () => pending.reject(new Error('Popup closed')))
    expect(online().getByRole('alert').textContent).toContain('Microsoft session may still be active')
    expect(loadFavourites().categories).toEqual(['News'])
    expect(checkbox('News').checked).toBe(true)
    await user.click(button('Retry Microsoft sign-out'))
    await screen.findByText(/Signed out of this app/)
  })

  it('aborts the access check on unmount and ignores its late result', async () => {
    const pending = deferred<Response>()
    fetchMock.mockReturnValue(pending.promise)
    cached = account
    const page = renderApp()
    await waitFor(() => expect(fetchMock).toHaveBeenCalledOnce())
    const signal = fetchMock.mock.calls[0][1]?.signal
    page.unmount()
    expect(signal?.aborted).toBe(true)
    await act(async () => pending.resolve(Response.json(body())))
    expect(screen.queryByText('Online access verified by the API.')).toBeNull()
  })
})
