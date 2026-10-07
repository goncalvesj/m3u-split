import { OnlineApiError } from './online-api'
import type { OnlineConfiguration } from './online-config'
import { formatBytes } from './online-playlist-utils'

export type ProfileRefreshState = 'queued' | 'running' | 'succeeded' | 'failed'
export type ProfileRefreshStage = 'downloading' | 'indexing' | 'filtering'

export interface ProfileFailure {
  code: string
  httpStatus: number | null
}

export interface ProfileRefresh {
  id: string
  status: ProfileRefreshState
  stage: ProfileRefreshStage | null
  queuedAt: number | null
  finishedAt: number | null
  bytesDownloaded: number
  totalBytes: number | null
  entries: number | null
  error: ProfileFailure | null
}

export interface ProfileIndex {
  indexedAt: number | null
  categoryCount: number
  entries: number
}

export interface ProfileOutput {
  id: string
  refreshedAt: number | null
  sizeBytes: number
  channels: number
  includedCategoryCount: number
  missingCategoryCount: number
  /** Selected categories that were not in the latest playlist. The API lists at most 100 names. */
  missingCategories: string[]
}

/** A saved profile. The provider URL itself is never returned by the API; only its scheme and host are. */
export interface OnlineProfile {
  id: string
  name: string
  source: string
  createdAt: number | null
  updatedAt: number | null
  selectedCount: number
  refresh: ProfileRefresh | null
  index: ProfileIndex | null
  output: ProfileOutput | null
}

export interface OnlineProfileList {
  profiles: OnlineProfile[]
  maxProfiles: number
  maxBytes: number | null
}

export interface ProfileCategory {
  name: string
  count: number
}

export interface ProfileCategories {
  categories: ProfileCategory[]
  entries: number
  indexedAt: number | null
  /** The saved selection, which can include categories that are missing from the latest playlist. */
  selected: string[]
}

export interface NewOnlineProfile {
  name: string
  url: string
  categories?: readonly string[]
}

export type OnlineProfileErrorKind = 'rejected' | 'not-found' | 'retryable'

/** A failed profile API call. The message is always client-written; server text is never shown. */
export class OnlineProfileError extends Error {
  readonly kind: OnlineProfileErrorKind
  readonly code: string

  constructor(kind: OnlineProfileErrorKind, code: string) {
    super(describeProfileFailure(code))
    this.name = 'OnlineProfileError'
    this.kind = kind
    this.code = code
  }
}

export const MAX_PROFILE_NAME_LENGTH = 80
export const MAX_PROFILE_CATEGORIES = 10_000
const MAX_FILE_NAME_LENGTH = 80
const REQUEST_TIMEOUT_MS = 30_000
// Azure closes HTTP requests that run longer than 230 seconds, so a longer wait would never succeed.
const DOWNLOAD_TIMEOUT_MS = 240_000
const REFRESH_STATES: readonly string[] = ['queued', 'running', 'succeeded', 'failed']
const REFRESH_STAGES: readonly string[] = ['downloading', 'indexing', 'filtering']
const ERROR_CODE = /^[a-z_]{1,64}$/
const PROFILE_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const SOURCE_DISPLAY = /^https?:\/\/[^/?#@\s]+$/i
const UNPRINTABLE = /[\p{Cc}\p{Cs}\p{Zl}\p{Zp}]/u
const FORMAT_CHARACTER = /^\p{Cf}$/u
const FILE_NAME_CHARACTER = /^[A-Za-z0-9 ._-]$/
const FILE_NAME_EDGES = /^[ ._-]+|[ ._-]+$/g
const FILE_NAME_END = /[ ._-]+$/

export const isProfileId = (value: string) => PROFILE_ID.test(value)

export const isRefreshActive = (profile: OnlineProfile) =>
  profile.refresh?.status === 'queued' || profile.refresh?.status === 'running'

/** Returns '' when the name can be saved, otherwise a message for the user. Mirrors the API's own check. */
export function checkProfileName(value: string): string {
  const name = value.trim()
  if (!name) return 'Enter a name for this profile.'
  if (name.length > MAX_PROFILE_NAME_LENGTH) {
    return `Use a shorter name. Profile names can have up to ${MAX_PROFILE_NAME_LENGTH} characters.`
  }
  if (UNPRINTABLE.test(name)) return 'Remove line breaks and other control characters from the name.'
  for (const character of name) {
    const code = character.codePointAt(0) ?? 0
    // Joiners and emoji tag characters are fine; bidirectional and other invisible controls are not.
    const allowed = code === 0x200c || code === 0x200d || (code >= 0xe0020 && code <= 0xe007f)
    if (!allowed && FORMAT_CHARACTER.test(character)) return 'Remove invisible formatting characters from the name.'
  }
  return ''
}

/** The same file name the API suggests for a profile playlist. Browsers do not expose that header here. */
export function profileFileName(name: string): string {
  let text = ''
  let replaced = false
  for (const character of name) {
    if (FILE_NAME_CHARACTER.test(character)) {
      text += (replaced ? '-' : '') + character
      replaced = false
    } else {
      replaced = true
    }
  }

  text = text.replace(FILE_NAME_EDGES, '')
  if (text.toLowerCase().endsWith('.m3u')) text = text.slice(0, -4).replace(FILE_NAME_END, '')
  if (text.length > MAX_FILE_NAME_LENGTH) text = text.slice(0, MAX_FILE_NAME_LENGTH).replace(FILE_NAME_END, '')
  return `${text || 'profile'}.m3u`
}

export function describeProfileFailure(code: string, httpStatus?: number | null, maxBytes?: number | null): string {
  switch (code) {
    case 'invalid_url':
      return 'Enter a complete http:// or https:// playlist URL. Credentials in the query string are fine, but a user name and password before the host name are not supported.'
    case 'blocked_destination':
      return 'This address is not allowed. Profiles only download from public internet addresses, never from local, private or cloud-internal networks.'
    case 'invalid_redirect':
      return 'The provider redirected to an address that cannot be used. Check the playlist URL with your provider.'
    case 'too_many_redirects':
      return 'The provider redirected too many times. Check the playlist URL with your provider.'
    case 'source_unreachable':
      return 'The server could not connect to the provider. Check the URL and that the provider is online, then try again.'
    case 'source_http_error':
      return describeSourceStatus(httpStatus)
    case 'source_timeout':
      return 'The provider took too long to send the playlist. Try again later.'
    case 'source_too_large':
      return maxBytes
        ? `The playlist is larger than the ${formatBytes(maxBytes)} download limit.`
        : 'The playlist is larger than the download size limit.'
    case 'not_m3u':
      return 'The URL did not return an M3U playlist (it must start with #EXTM3U). Check that you copied the M3U playlist link from your provider.'
    case 'no_categories':
      return 'The playlist has no categories (group-title values), so there is nothing to select.'
    case 'too_many_categories':
      return 'The playlist has more categories than a profile can handle.'
    case 'no_matching_channels':
      return 'None of the selected categories has channels in the latest playlist. Edit the categories and save again.'
    case 'refresh_interrupted':
      return 'The refresh stopped before it finished. Refresh again.'
    case 'internal_error':
      return 'The refresh failed because of a server problem. Try again.'
    case 'invalid_request':
      return 'The online API did not accept the request. Reload the page and try again.'
    case 'invalid_name':
      return `Enter a profile name of 1 to ${MAX_PROFILE_NAME_LENGTH} printable characters.`
    case 'invalid_selection':
      return 'Some selected categories are not in the latest playlist. Reload the categories and choose again.'
    case 'profile_not_found':
      return 'This profile no longer exists. It may have been deleted in another window.'
    case 'profile_limit_reached':
      return 'You have reached the maximum number of saved profiles. Delete one before adding another.'
    case 'profile_not_indexed':
      return 'This profile has no category list yet. Wait for its first refresh to finish, or refresh it again.'
    case 'refresh_in_progress':
      return 'This profile is refreshing. Wait for the refresh to finish, then try again.'
    case 'output_not_found':
      return 'This profile has no filtered playlist yet. Choose categories and let it refresh first.'
    case 'profiles_not_configured':
      return 'Saved profiles are not set up on the online API yet. Local files still work.'
    case 'profiles_unavailable':
      return 'Saved profile storage is temporarily unavailable. Try again in a moment.'
    case 'identity_unavailable':
      return 'The online API could not check your sign-in right now. Try again in a moment.'
    case 'online_not_configured':
      return 'Online access is not configured on the server. Local files still work.'
    case 'rate_limited':
      return 'The online API received too many requests. Wait one minute and try again.'
    case 'rate_limits_not_configured':
      return 'The rate limits of the online API are not configured correctly on the server. Local files still work.'
    case 'server_error':
      return 'The online API had a problem. Try again in a moment.'
    case 'busy':
      return 'The online API is busy. Try again in a moment.'
    case 'network':
      return 'Could not reach the online API, or the connection was interrupted. Check your connection and try again. Local files still work.'
    case 'timeout':
      return 'The online API did not respond in time. Try again.'
    case 'invalid_response':
      return 'The online API returned an unexpected response. Try again.'
    case 'token_unavailable':
      return 'Could not get a fresh Microsoft access token. Check your connection and try again.'
    default:
      return 'The profile request could not be completed. Try again.'
  }
}

function describeSourceStatus(status?: number | null): string {
  if (status === 401 || status === 403) {
    return `The provider refused access (HTTP ${status}). Check that your subscription is active and the credentials in the URL are correct.`
  }
  if (status === 404) return 'The provider returned HTTP 404 (not found). Check the playlist URL.'
  if (status === 429) return 'The provider is limiting requests (HTTP 429). Wait a few minutes, then try again.'
  if (status && status >= 500) return `The provider's server had a problem (HTTP ${status}). Try again later.`
  if (status) return `The provider returned HTTP ${status}. Check the playlist URL.`
  return 'The provider returned an error. Check the playlist URL.'
}

export async function listProfiles(
  config: OnlineConfiguration,
  accessToken: string,
  signal?: AbortSignal,
): Promise<OnlineProfileList> {
  return callProfileApi(config, accessToken, { path: 'online/profiles', signal },
    async response => parseList(await readJson(response)))
}

export async function getProfile(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  signal?: AbortSignal,
): Promise<OnlineProfile> {
  return callProfileApi(config, accessToken, { path: profilePath(profileId), signal },
    async response => parseProfile(await readJson(response), profileId))
}

/** Saves a new profile. The API queues its first refresh straight away. */
export async function createProfile(
  config: OnlineConfiguration,
  accessToken: string,
  profile: NewOnlineProfile,
  signal?: AbortSignal,
): Promise<OnlineProfile> {
  const body = profile.categories?.length
    ? { name: profile.name, url: profile.url, categories: profile.categories }
    : { name: profile.name, url: profile.url }
  return callProfileApi(config, accessToken, { path: 'online/profiles', method: 'POST', body, signal },
    async response => parseProfile(await readJson(response)))
}

export async function getProfileCategories(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  signal?: AbortSignal,
): Promise<ProfileCategories> {
  return callProfileApi(config, accessToken, { path: profilePath(profileId, 'categories'), signal },
    async response => parseCategories(await readJson(response)))
}

/** Saves the selected categories and queues a refresh that rebuilds the filtered playlist. */
export async function saveProfileCategories(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  categories: readonly string[],
  signal?: AbortSignal,
): Promise<OnlineProfile> {
  return callProfileApi(config, accessToken,
    { path: profilePath(profileId, 'categories'), method: 'POST', body: { categories }, signal },
    async response => parseProfile(await readJson(response), profileId))
}

export async function renameProfile(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  name: string,
  signal?: AbortSignal,
): Promise<OnlineProfile> {
  return callProfileApi(config, accessToken,
    { path: profilePath(profileId, 'rename'), method: 'POST', body: { name }, signal },
    async response => parseProfile(await readJson(response), profileId))
}

/** Replaces the provider URL stored with the profile and queues a refresh from the new URL. */
export async function changeProfileSource(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  url: string,
  signal?: AbortSignal,
): Promise<OnlineProfile> {
  return callProfileApi(config, accessToken,
    { path: profilePath(profileId, 'source'), method: 'POST', body: { url }, signal },
    async response => parseProfile(await readJson(response), profileId))
}

/** Queues a refresh. While a refresh is already queued or running, the API returns that one instead. */
export async function refreshProfile(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  signal?: AbortSignal,
): Promise<OnlineProfile> {
  return callProfileApi(config, accessToken, { path: profilePath(profileId, 'refresh'), method: 'POST', signal },
    async response => parseProfile(await readJson(response), profileId))
}

/** Downloads the latest filtered playlist of the profile as a file ready to save. */
export async function downloadProfileOutput(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  signal?: AbortSignal,
): Promise<Blob> {
  return callProfileApi(config, accessToken, {
    path: profilePath(profileId, 'output'),
    accept: 'audio/x-mpegurl, application/json',
    timeout: DOWNLOAD_TIMEOUT_MS,
    signal,
  }, async response => {
    const bytes = await response.arrayBuffer()
    const start = new TextDecoder().decode(new Uint8Array(bytes, 0, Math.min(bytes.byteLength, 16)))
    if (!/^\uFEFF?#EXTM3U/i.test(start)) throw invalidResponse()
    return new Blob([bytes], { type: 'audio/x-mpegurl' })
  })
}

/** Deletes the profile, its stored URL and every file kept for it. Profiles that are already gone count as deleted. */
export async function deleteProfile(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  signal?: AbortSignal,
): Promise<void> {
  try {
    await callProfileApi(config, accessToken, { path: profilePath(profileId), method: 'DELETE', signal }, async () => undefined)
  } catch (error) {
    if (error instanceof OnlineProfileError && error.kind === 'not-found') return
    throw error
  }
}

interface ProfileRequest {
  path: string
  method?: 'GET' | 'POST' | 'DELETE'
  body?: unknown
  accept?: string
  timeout?: number
  signal?: AbortSignal
}

async function callProfileApi<T>(
  config: OnlineConfiguration,
  accessToken: string,
  request: ProfileRequest,
  read: (response: Response) => Promise<T>,
): Promise<T> {
  const { signal } = request
  const controller = new AbortController()
  const abort = () => controller.abort()
  signal?.addEventListener('abort', abort, { once: true })
  if (signal?.aborted) controller.abort()
  let timedOut = false
  const timeout = window.setTimeout(() => {
    timedOut = true
    controller.abort()
  }, request.timeout ?? REQUEST_TIMEOUT_MS)

  const headers: Record<string, string> = { Accept: request.accept ?? 'application/json' }
  headers.Authorization = `Bearer ${accessToken}`
  if (request.body !== undefined) headers['Content-Type'] = 'application/json'

  try {
    const response = await fetch(new URL(request.path, config.apiBaseUrl), {
      method: request.method ?? 'GET',
      headers,
      body: request.body === undefined ? undefined : JSON.stringify(request.body),
      credentials: 'omit',
      cache: 'no-store',
      redirect: 'error',
      signal: controller.signal,
    })
    if (!response.ok) throw await failure(response)
    return await read(response)
  } catch (error) {
    if (signal?.aborted || error instanceof OnlineApiError || error instanceof OnlineProfileError) throw error
    throw new OnlineProfileError('retryable', timedOut ? 'timeout' : 'network')
  } finally {
    window.clearTimeout(timeout)
    signal?.removeEventListener('abort', abort)
  }
}

async function failure(response: Response): Promise<Error> {
  const { status } = response
  if (status === 401) {
    return new OnlineApiError('unauthorized', 'The online API rejected this session. Sign in again to request a fresh API access token.')
  }
  if (status === 403) {
    return new OnlineApiError('forbidden', 'Microsoft sign-in succeeded, but this account or application is not permitted to use online playlists. Ask the owner to check the allowed account and API permission.')
  }

  const code = await errorCode(response)
  // A 404 without one of the API's own codes means the profile endpoints are not deployed.
  if (status === 404) {
    if (code === 'profile_not_found') return new OnlineProfileError('not-found', code)
    return new OnlineProfileError('rejected', code === 'output_not_found' ? code : 'profiles_not_configured')
  }
  if (status === 503 && code === 'profiles_not_configured') return new OnlineProfileError('rejected', code)
  if (status >= 400 && status < 500 && status !== 408 && status !== 429) {
    return new OnlineProfileError('rejected', code ?? 'invalid_request')
  }
  return new OnlineProfileError('retryable', code ?? (status >= 500 ? 'server_error' : 'busy'))
}

async function errorCode(response: Response): Promise<string | null> {
  try {
    const body: unknown = await response.json()
    return isRecord(body) && typeof body.error === 'string' && ERROR_CODE.test(body.error) ? body.error : null
  } catch {
    return null
  }
}

async function readJson(response: Response): Promise<unknown> {
  try {
    return await response.json()
  } catch (error) {
    if (error instanceof SyntaxError) throw invalidResponse()
    throw error
  }
}

function profilePath(profileId: string, action?: string) {
  if (!isProfileId(profileId)) throw new OnlineProfileError('not-found', 'profile_not_found')
  return `online/profiles/${profileId}${action ? `/${action}` : ''}`
}

const invalidResponse = () => new OnlineProfileError('retryable', 'invalid_response')

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const isRefreshState = (value: unknown): value is ProfileRefreshState =>
  typeof value === 'string' && REFRESH_STATES.includes(value)

const isRefreshStage = (value: unknown): value is ProfileRefreshStage =>
  typeof value === 'string' && REFRESH_STAGES.includes(value)

const count = (value: unknown) =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null

function parseTimestamp(value: unknown): number | null {
  if (typeof value !== 'string') return null
  // .NET sends seven fractional digits; trim to milliseconds so every browser can parse the value.
  const time = Date.parse(value.replace(/(\.\d{3})\d+/, '$1'))
  return Number.isFinite(time) ? time : null
}

function parseId(value: unknown): string {
  const id = typeof value === 'string' ? value.toLowerCase() : ''
  if (!isProfileId(id)) throw invalidResponse()
  return id
}

function parseFailure(value: unknown): ProfileFailure {
  if (!isRecord(value) || typeof value.code !== 'string' || !ERROR_CODE.test(value.code)) {
    return { code: 'internal_error', httpStatus: null }
  }
  const status = value.httpStatus
  return {
    code: value.code,
    httpStatus: typeof status === 'number' && Number.isInteger(status) && status >= 100 && status <= 599 ? status : null,
  }
}

function parseList(body: unknown): OnlineProfileList {
  if (!isRecord(body) || !Array.isArray(body.profiles)) throw invalidResponse()
  const maxProfiles = count(body.maxProfiles)
  if (!maxProfiles) throw invalidResponse()

  const ids = new Set<string>()
  const profiles = body.profiles.map(item => {
    const profile = parseProfile(item)
    if (ids.has(profile.id)) throw invalidResponse()
    ids.add(profile.id)
    return profile
  })
  return { profiles, maxProfiles, maxBytes: count(body.maxDownloadBytes) || null }
}

function parseProfile(body: unknown, expectedId?: string): OnlineProfile {
  if (!isRecord(body) || typeof body.name !== 'string' || !body.name.trim()) throw invalidResponse()
  const id = parseId(body.id)
  if (expectedId !== undefined && id !== expectedId) throw invalidResponse()

  return {
    id,
    name: body.name,
    source: typeof body.sourceDisplay === 'string' && SOURCE_DISPLAY.test(body.sourceDisplay) ? body.sourceDisplay : '',
    createdAt: parseTimestamp(body.createdAt),
    updatedAt: parseTimestamp(body.updatedAt),
    selectedCount: count(body.selectedCount) ?? 0,
    refresh: body.refresh == null ? null : parseRefresh(body.refresh),
    index: body.index == null ? null : parseIndex(body.index),
    output: body.output == null ? null : parseOutput(body.output),
  }
}

function parseRefresh(value: unknown): ProfileRefresh {
  if (!isRecord(value) || !isRefreshState(value.status)) throw invalidResponse()
  return {
    id: parseId(value.refreshId),
    status: value.status,
    stage: isRefreshStage(value.stage) ? value.stage : null,
    queuedAt: parseTimestamp(value.queuedAt),
    finishedAt: parseTimestamp(value.finishedAt),
    bytesDownloaded: count(value.bytesDownloaded) ?? 0,
    totalBytes: count(value.totalBytes),
    entries: count(value.entries),
    error: value.status === 'failed' ? parseFailure(value.error) : null,
  }
}

function parseIndex(value: unknown): ProfileIndex {
  if (!isRecord(value)) throw invalidResponse()
  return {
    indexedAt: parseTimestamp(value.indexedAt),
    categoryCount: count(value.categoryCount) ?? 0,
    entries: count(value.entries) ?? 0,
  }
}

function parseOutput(value: unknown): ProfileOutput {
  if (!isRecord(value)) throw invalidResponse()
  const missing = Array.isArray(value.missingCategories)
    ? value.missingCategories.filter((name): name is string => typeof name === 'string' && name.length > 0)
    : []
  return {
    id: parseId(value.outputId),
    refreshedAt: parseTimestamp(value.refreshedAt),
    sizeBytes: count(value.sizeBytes) ?? 0,
    channels: count(value.channels) ?? 0,
    includedCategoryCount: count(value.includedCategoryCount) ?? 0,
    missingCategoryCount: Math.max(count(value.missingCategoryCount) ?? 0, missing.length),
    missingCategories: missing,
  }
}

function parseCategories(body: unknown): ProfileCategories {
  if (!isRecord(body) || !Array.isArray(body.categories) || !Array.isArray(body.selected)) throw invalidResponse()
  const names = new Set<string>()
  const categories: ProfileCategory[] = []
  for (const item of body.categories) {
    const channels = isRecord(item) ? count(item.count) : null
    if (!isRecord(item) || typeof item.name !== 'string' || !item.name.length || names.has(item.name) || channels === null) {
      throw invalidResponse()
    }
    names.add(item.name)
    categories.push({ name: item.name, count: channels })
  }

  const selected = new Set<string>()
  for (const name of body.selected) {
    if (typeof name !== 'string' || !name.length) throw invalidResponse()
    selected.add(name)
  }
  return { categories, entries: count(body.entries) ?? 0, indexedAt: parseTimestamp(body.indexedAt), selected: [...selected] }
}
