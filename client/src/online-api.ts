import { isIdentityId } from './online-config'
import type { OnlineConfiguration } from './online-config'

export interface OnlineIdentity {
  tenantId: string
  objectId: string
  expiresAt: number
}

export class OnlineApiError extends Error {
  readonly kind: 'unauthorized' | 'forbidden' | 'unavailable'

  constructor(kind: OnlineApiError['kind'], message: string) {
    super(message)
    this.kind = kind
  }
}

export async function fetchOnlineSession(
  config: OnlineConfiguration,
  accessToken: string,
  signal: AbortSignal,
): Promise<OnlineIdentity> {
  const controller = new AbortController()
  const abort = () => controller.abort()
  signal.addEventListener('abort', abort, { once: true })
  if (signal.aborted) controller.abort()
  const timeout = window.setTimeout(abort, 15_000)

  try {
    const response = await fetch(new URL('online/session', config.apiBaseUrl), {
      headers: { Authorization: `Bearer ${accessToken}`, Accept: 'application/json' },
      credentials: 'omit',
      cache: 'no-store',
      redirect: 'error',
      signal: controller.signal,
    })
    if (response.status === 401) {
      throw new OnlineApiError('unauthorized', 'The online API rejected this session. Sign in again to request a fresh API access token.')
    }
    if (response.status === 403) {
      throw new OnlineApiError('forbidden', 'Microsoft sign-in succeeded, but this account or application is not permitted to use online playlists. Ask the owner to check the allowed account and API permission.')
    }
    if (!response.ok) {
      throw new OnlineApiError('unavailable', 'The online API is unavailable or not configured. Online access has not been verified.')
    }

    const body: unknown = await response.json()
    if (typeof body !== 'object' || body === null ||
        !('tenantId' in body) || body.tenantId !== config.tenantId ||
        !('objectId' in body) || typeof body.objectId !== 'string' || !isIdentityId(body.objectId) ||
        !('expiresAt' in body) || typeof body.expiresAt !== 'string' ||
        !Number.isFinite(Date.parse(body.expiresAt)) || Date.parse(body.expiresAt) <= Date.now()) {
      throw new OnlineApiError('unavailable', 'The online API returned an invalid session response. Online access has not been verified.')
    }
    return { tenantId: body.tenantId, objectId: body.objectId, expiresAt: Date.parse(body.expiresAt) }
  } catch (error) {
    if (signal.aborted || error instanceof OnlineApiError) throw error
    throw new OnlineApiError('unavailable', 'Could not reach or read the online API. Check your connection and try again; local files still work.')
  } finally {
    window.clearTimeout(timeout)
    signal.removeEventListener('abort', abort)
  }
}
