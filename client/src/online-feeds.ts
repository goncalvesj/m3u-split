import { OnlineApiError } from './online-api'
import type { OnlineConfiguration } from './online-config'
import { isProfileId } from './online-profiles'

/** The secret TV link of a saved profile. Anyone who has the token can download the profile's latest playlist. */
export interface OnlineFeedLink {
  token: string
  createdAt: number | null
}

export type OnlineFeedErrorKind = 'rejected' | 'not-found' | 'retryable'

/** A failed TV link API call. The message is always client-written; server text is never shown. */
export class OnlineFeedError extends Error {
  readonly kind: OnlineFeedErrorKind
  readonly code: string

  constructor(kind: OnlineFeedErrorKind, code: string) {
    super(describeFeedFailure(code))
    this.name = 'OnlineFeedError'
    this.kind = kind
    this.code = code
  }
}

const REQUEST_TIMEOUT_MS = 30_000
const ERROR_CODE = /^[a-z_]{1,64}$/
const FEED_TOKEN = /^[A-Za-z0-9_-]{43}$/

/** The address Smart IPTV loads. It works without sign-in, so it is as sensitive as the provider URL. */
export const feedUrl = (config: OnlineConfiguration, token: string) =>
  new URL('feed/' + token + '.m3u', config.apiBaseUrl).toString()

export function describeFeedFailure(code: string): string {
  switch (code) {
    case 'feed_not_found':
      return 'This profile has no TV link any more. It may have been revoked in another window.'
    case 'profile_not_found':
      return 'This profile no longer exists. It may have been deleted in another window.'
    case 'feeds_not_configured':
      return 'TV links are not available on this server. Saved profiles and playlist downloads still work.'
    case 'feeds_unavailable':
      return 'TV link storage is temporarily unavailable. Try again in a moment.'
    case 'invalid_request':
      return 'The online API did not accept the request. Reload the page and try again.'
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
      return 'Could not reach the online API, or the connection was interrupted. Check your connection and try again.'
    case 'timeout':
      return 'The online API did not respond in time. Try again.'
    case 'invalid_response':
      return 'The online API returned an unexpected response. Try again.'
    case 'token_unavailable':
      return 'Could not get a fresh Microsoft access token. Check your connection and try again.'
    default:
      return 'The TV link request could not be completed. Try again.'
  }
}

/** Returns the profile's TV link, or null when the profile has none. */
export async function getFeedLink(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  signal?: AbortSignal,
): Promise<OnlineFeedLink | null> {
  try {
    return await callFeedApi(config, accessToken, { path: feedPath(profileId), signal }, readLink)
  } catch (error) {
    if (error instanceof OnlineFeedError && error.code === 'feed_not_found') return null
    throw error
  }
}

/** Creates the profile's TV link. When the profile already has one, the API returns that link instead. */
export async function createFeedLink(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  signal?: AbortSignal,
): Promise<OnlineFeedLink> {
  return callFeedApi(config, accessToken, { path: feedPath(profileId), method: 'POST', signal }, readLink)
}

/** Replaces the TV link with a new one. The old link stops working straight away. */
export async function regenerateFeedLink(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  signal?: AbortSignal,
): Promise<OnlineFeedLink> {
  return callFeedApi(config, accessToken, { path: feedPath(profileId, 'regenerate'), method: 'POST', signal }, readLink)
}

/** Turns the TV link off straight away. A profile without a link counts as revoked. */
export async function revokeFeedLink(
  config: OnlineConfiguration,
  accessToken: string,
  profileId: string,
  signal?: AbortSignal,
): Promise<void> {
  await callFeedApi(config, accessToken, { path: feedPath(profileId), method: 'DELETE', signal }, async () => undefined)
}

interface FeedRequest {
  path: string
  method?: 'GET' | 'POST' | 'DELETE'
  signal?: AbortSignal
}

async function callFeedApi<T>(
  config: OnlineConfiguration,
  accessToken: string,
  request: FeedRequest,
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
  }, REQUEST_TIMEOUT_MS)

  try {
    const response = await fetch(new URL(request.path, config.apiBaseUrl), {
      method: request.method ?? 'GET',
      headers: { Accept: 'application/json', Authorization: `Bearer ${accessToken}` },
      credentials: 'omit',
      cache: 'no-store',
      redirect: 'error',
      signal: controller.signal,
    })
    if (!response.ok) throw await failure(response)
    return await read(response)
  } catch (error) {
    if (signal?.aborted || error instanceof OnlineApiError || error instanceof OnlineFeedError) throw error
    throw new OnlineFeedError('retryable', timedOut ? 'timeout' : 'network')
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
  // A 404 without one of the API's own codes means the TV link endpoints are not deployed.
  if (status === 404) {
    if (code === 'profile_not_found') return new OnlineFeedError('not-found', code)
    return new OnlineFeedError('rejected', code === 'feed_not_found' ? code : 'feeds_not_configured')
  }
  if (status === 503 && code === 'feeds_not_configured') return new OnlineFeedError('rejected', code)
  if (status >= 400 && status < 500 && status !== 408 && status !== 429) {
    return new OnlineFeedError('rejected', code ?? 'invalid_request')
  }
  return new OnlineFeedError('retryable', code ?? (status >= 500 ? 'server_error' : 'busy'))
}

async function errorCode(response: Response): Promise<string | null> {
  try {
    const body: unknown = await response.json()
    return isRecord(body) && typeof body.error === 'string' && ERROR_CODE.test(body.error) ? body.error : null
  } catch {
    return null
  }
}

async function readLink(response: Response): Promise<OnlineFeedLink> {
  let body: unknown
  try {
    body = await response.json()
  } catch (error) {
    if (error instanceof SyntaxError) throw invalidResponse()
    throw error
  }
  if (!isRecord(body) || typeof body.token !== 'string' || !FEED_TOKEN.test(body.token)) throw invalidResponse()
  return { token: body.token, createdAt: parseTimestamp(body.createdAt) }
}

function feedPath(profileId: string, action?: string) {
  if (!isProfileId(profileId)) throw new OnlineFeedError('not-found', 'profile_not_found')
  return `online/profiles/${profileId}/feed${action ? `/${action}` : ''}`
}

const invalidResponse = () => new OnlineFeedError('retryable', 'invalid_response')

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

function parseTimestamp(value: unknown): number | null {
  if (typeof value !== 'string') return null
  // .NET sends seven fractional digits; trim to milliseconds so every browser can parse the value.
  const time = Date.parse(value.replace(/(\.\d{3})\d+/, '$1'))
  return Number.isFinite(time) ? time : null
}
