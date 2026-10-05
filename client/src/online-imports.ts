import { OnlineApiError } from './online-api'
import type { OnlineConfiguration } from './online-config'

export type OnlineImportState = 'queued' | 'running' | 'ready' | 'failed'
export type OnlineImportStage = 'downloading' | 'indexing'

export interface OnlineImportFailure {
  code: string
  httpStatus: number | null
}

export interface OnlineImportStatus {
  id: string
  status: OnlineImportState
  stage: OnlineImportStage | null
  /** Redacted scheme://host[:port] form of the source URL, or '' when the API did not return a safe value. */
  source: string
  expiresAt: number | null
  bytesDownloaded: number
  totalBytes: number | null
  maxBytes: number | null
  entries: number
  categoryCount: number
  error: OnlineImportFailure | null
}

export interface OnlineImportCategory {
  name: string
  count: number
}

export interface OnlineImportCategories {
  categories: OnlineImportCategory[]
  entries: number
}

export type OnlineImportErrorKind = 'rejected' | 'not-found' | 'expired' | 'retryable'

/** A failed import API call. The message is always client-written; server text is never shown. */
export class OnlineImportError extends Error {
  readonly kind: OnlineImportErrorKind
  readonly code: string

  constructor(kind: OnlineImportErrorKind, code: string) {
    super(describeImportFailure(code))
    this.name = 'OnlineImportError'
    this.kind = kind
    this.code = code
  }
}

export const IMPORT_FILE_NAME = 'channels.m3u'
export const MAX_SOURCE_URL_LENGTH = 2048
const REQUEST_TIMEOUT_MS = 30_000
// Azure closes HTTP requests that run longer than 230 seconds, so a longer wait would never succeed.
const DOWNLOAD_TIMEOUT_MS = 240_000
const IMPORT_STATES: readonly string[] = ['queued', 'running', 'ready', 'failed']
const ERROR_CODE = /^[a-z_]{1,64}$/
const IMPORT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const SOURCE_DISPLAY = /^https?:\/\/[^/?#@\s]+$/i
const INTERACTION_CODES = ['interaction_required', 'consent_required', 'login_required', 'no_account_error']

export const isImportId = (value: string) => IMPORT_ID.test(value)

/** Returns '' when the URL can be sent to the API, otherwise a message for the user. */
export function checkSourceUrl(value: string): string {
  const text = value.trim()
  if (!text) return 'Enter the playlist URL from your provider.'
  if (text.length > MAX_SOURCE_URL_LENGTH) {
    return `This URL is too long. Playlist URLs can have up to ${MAX_SOURCE_URL_LENGTH} characters.`
  }

  let url: URL
  try {
    url = new URL(text)
  } catch {
    return 'Enter a complete playlist URL that starts with http:// or https://.'
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    return 'Only http:// and https:// playlist URLs can be imported.'
  }
  if (url.username || url.password) {
    return 'Remove the user name and password before the host name (user:password@). Credentials in the query string, such as ?username=...&password=..., are fine.'
  }
  if (text.includes('#')) return 'Remove the # part from the end of the URL.'
  return ''
}

/** True for MSAL failures that need a new interactive sign-in rather than a retry. */
export function needsInteraction(error: unknown): boolean {
  if (typeof error !== 'object' || error === null) return false
  if ('name' in error && error.name === 'InteractionRequiredAuthError') return true
  return 'errorCode' in error && INTERACTION_CODES.includes(String(error.errorCode))
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`
}

export const formatCount = (value: number) => value.toLocaleString()

export const formatTime = (time: number) =>
  new Date(time).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })

export function describeImportFailure(code: string, httpStatus?: number | null, maxBytes?: number | null): string {
  switch (code) {
    case 'invalid_url':
      return 'Enter a complete http:// or https:// playlist URL. Credentials in the query string are fine, but a user name and password before the host name are not supported.'
    case 'blocked_destination':
      return 'This address is not allowed. Imports only download from public internet addresses, never from local, private or cloud-internal networks.'
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
        ? `The playlist is larger than the ${formatBytes(maxBytes)} import limit.`
        : 'The playlist is larger than the import size limit.'
    case 'not_m3u':
      return 'The URL did not return an M3U playlist (it must start with #EXTM3U). Check that you copied the M3U playlist link from your provider.'
    case 'no_categories':
      return 'The playlist has no categories (group-title values), so there is nothing to select.'
    case 'too_many_categories':
      return 'The playlist has more categories than an import can handle.'
    case 'import_interrupted':
      return 'The import stopped before it finished. Import the playlist again.'
    case 'internal_error':
      return 'The import failed because of a server problem. Try again.'
    case 'invalid_request':
      return 'The online API did not accept the request. Reload the page and try again.'
    case 'invalid_selection':
      return 'None of the selected categories has channels in this import. Select at least one category with channels.'
    case 'import_not_ready':
      return 'This import is not ready yet. Wait for it to finish.'
    case 'import_not_found':
      return 'This import no longer exists. It may have been discarded, replaced by a newer import or deleted after it expired. Import the playlist again.'
    case 'import_expired':
      return 'This import has expired and was deleted from the server. Import the playlist again.'
    case 'import_not_configured':
      return 'URL import is not set up on the online API yet. Local files still work.'
    case 'import_unavailable':
      return 'Online playlist storage is temporarily unavailable. Try again in a moment.'
    case 'identity_unavailable':
      return 'The online API could not check your sign-in right now. Try again in a moment.'
    case 'online_not_configured':
      return 'Online access is not configured on the server. Local files still work.'
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
      return 'The import could not be completed. Try again.'
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

export async function startImport(
  config: OnlineConfiguration,
  accessToken: string,
  url: string,
  signal?: AbortSignal,
): Promise<OnlineImportStatus> {
  return callImportApi(config, accessToken, { path: 'online/imports', method: 'POST', body: { url }, signal },
    async response => parseStatus(await readJson(response)))
}

export async function getImportStatus(
  config: OnlineConfiguration,
  accessToken: string,
  importId: string,
  signal?: AbortSignal,
): Promise<OnlineImportStatus> {
  return callImportApi(config, accessToken, { path: importPath(importId), signal },
    async response => parseStatus(await readJson(response), importId))
}

export async function getImportCategories(
  config: OnlineConfiguration,
  accessToken: string,
  importId: string,
  signal?: AbortSignal,
): Promise<OnlineImportCategories> {
  return callImportApi(config, accessToken, { path: importPath(importId, 'categories'), signal },
    async response => parseCategories(await readJson(response)))
}

/** Asks the API to build the filtered playlist and returns it as a file ready to save. */
export async function downloadImportPlaylist(
  config: OnlineConfiguration,
  accessToken: string,
  importId: string,
  categories: readonly string[],
  signal?: AbortSignal,
): Promise<Blob> {
  return callImportApi(config, accessToken, {
    path: importPath(importId, 'playlist'),
    method: 'POST',
    body: { categories },
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

/** Deletes the import and its data on the server. Imports that are already gone count as discarded. */
export async function discardImport(
  config: OnlineConfiguration,
  accessToken: string,
  importId: string,
  signal?: AbortSignal,
): Promise<void> {
  try {
    await callImportApi(config, accessToken, { path: importPath(importId), method: 'DELETE', signal }, async () => undefined)
  } catch (error) {
    if (error instanceof OnlineImportError && (error.kind === 'not-found' || error.kind === 'expired')) return
    throw error
  }
}

interface ImportRequest {
  path: string
  method?: 'GET' | 'POST' | 'DELETE'
  body?: unknown
  accept?: string
  timeout?: number
  signal?: AbortSignal
}

async function callImportApi<T>(
  config: OnlineConfiguration,
  accessToken: string,
  request: ImportRequest,
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
    if (signal?.aborted || error instanceof OnlineApiError || error instanceof OnlineImportError) throw error
    throw new OnlineImportError('retryable', timedOut ? 'timeout' : 'network')
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
  // A 404 without the API's own code means the import endpoints are not deployed.
  if (status === 404) {
    return code === 'import_not_found'
      ? new OnlineImportError('not-found', code)
      : new OnlineImportError('rejected', 'import_not_configured')
  }
  if (status === 410) return new OnlineImportError('expired', 'import_expired')
  if (status === 503 && code === 'import_not_configured') return new OnlineImportError('rejected', code)
  if (status >= 400 && status < 500 && status !== 408 && status !== 429) {
    return new OnlineImportError('rejected', code ?? 'invalid_request')
  }
  return new OnlineImportError('retryable', code ?? (status >= 500 ? 'server_error' : 'busy'))
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

function importPath(importId: string, action?: string) {
  if (!isImportId(importId)) throw new OnlineImportError('not-found', 'import_not_found')
  return `online/imports/${importId}${action ? `/${action}` : ''}`
}

const invalidResponse = () => new OnlineImportError('retryable', 'invalid_response')

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const isImportState = (value: unknown): value is OnlineImportState =>
  typeof value === 'string' && IMPORT_STATES.includes(value)

const count = (value: unknown) =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null

function parseTimestamp(value: unknown): number | null {
  if (typeof value !== 'string') return null
  // .NET sends seven fractional digits; trim to milliseconds so every browser can parse the value.
  const time = Date.parse(value.replace(/(\.\d{3})\d+/, '$1'))
  return Number.isFinite(time) ? time : null
}

function parseFailure(value: unknown): OnlineImportFailure {
  if (!isRecord(value) || typeof value.code !== 'string' || !ERROR_CODE.test(value.code)) {
    return { code: 'internal_error', httpStatus: null }
  }
  const status = value.httpStatus
  return {
    code: value.code,
    httpStatus: typeof status === 'number' && Number.isInteger(status) && status >= 100 && status <= 599 ? status : null,
  }
}

function parseStatus(body: unknown, expectedId?: string): OnlineImportStatus {
  if (!isRecord(body) || typeof body.id !== 'string' || !isImportState(body.status)) throw invalidResponse()
  const id = body.id.toLowerCase()
  if (!isImportId(id) || (expectedId !== undefined && id !== expectedId)) throw invalidResponse()

  return {
    id,
    status: body.status,
    stage: body.stage === 'downloading' || body.stage === 'indexing' ? body.stage : null,
    source: typeof body.source === 'string' && SOURCE_DISPLAY.test(body.source) ? body.source : '',
    expiresAt: parseTimestamp(body.expiresAt),
    bytesDownloaded: count(body.bytesDownloaded) ?? 0,
    totalBytes: count(body.totalBytes),
    maxBytes: count(body.maxBytes),
    entries: count(body.entries) ?? 0,
    categoryCount: count(body.categoryCount) ?? 0,
    error: body.status === 'failed' ? parseFailure(body.error) : null,
  }
}

function parseCategories(body: unknown): OnlineImportCategories {
  if (!isRecord(body) || !Array.isArray(body.categories)) throw invalidResponse()
  const names = new Set<string>()
  const categories: OnlineImportCategory[] = []
  for (const item of body.categories) {
    const channels = isRecord(item) ? count(item.count) : null
    if (!isRecord(item) || typeof item.name !== 'string' || !item.name.length || names.has(item.name) || channels === null) {
      throw invalidResponse()
    }
    names.add(item.name)
    categories.push({ name: item.name, count: channels })
  }
  return { categories, entries: count(body.entries) ?? 0 }
}
