import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchOnlineSession, OnlineApiError } from './online-api'
import type { OnlineConfiguration } from './online-config'

const config: OnlineConfiguration = {
  tenantId: '11111111-1111-4111-8111-111111111111',
  clientId: '22222222-2222-4222-8222-222222222222',
  apiScope: 'api://33333333-3333-4333-8333-333333333333/access_as_user',
  apiBaseUrl: 'https://function.example.invalid/api/',
  redirectUri: 'https://goncalvesj.github.io/m3u-split/auth.html',
}
const session = () => ({
  tenantId: config.tenantId,
  objectId: '44444444-4444-4444-8444-444444444444',
  expiresAt: new Date(Date.now() + 60_000).toISOString(),
})
const fetchMock = vi.fn<typeof fetch>()
const call = (signal = new AbortController().signal) => fetchOnlineSession(config, 'api-access-token', signal)

beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.useRealTimers()
})

describe('protected session client', () => {
  it('uses only the API bearer token and never sends cookies, identity parameters or function keys', async () => {
    const body = session()
    fetchMock.mockResolvedValue(Response.json(body))
    expect(await call()).toEqual({ ...body, expiresAt: Date.parse(body.expiresAt) })
    const [url, options] = fetchMock.mock.calls[0]
    expect(String(url)).toBe('https://function.example.invalid/api/online/session')
    expect(options).toEqual({
      headers: { Authorization: 'Bearer api-access-token', Accept: 'application/json' },
      credentials: 'omit',
      cache: 'no-store',
      redirect: 'error',
      signal: expect.any(AbortSignal),
    })
  })

  it.each([
    [401, 'unauthorized'],
    [403, 'forbidden'],
    [404, 'unavailable'],
    [429, 'unavailable'],
    [500, 'unavailable'],
    [503, 'unavailable'],
  ])('does not turn HTTP %s into a successful session', async (status, kind) => {
    fetchMock.mockResolvedValue(new Response('', { status }))
    await expect(call()).rejects.toMatchObject({ kind })
  })

  it('tells the user to wait when the API rate limit rejects the session check', async () => {
    fetchMock.mockResolvedValue(Response.json({ error: 'rate_limited' }, { status: 429, headers: { 'Retry-After': '60' } }))
    await expect(call()).rejects.toMatchObject({
      kind: 'unavailable',
      message: 'The online API received too many requests. Wait one minute and try again.',
    })
  })

  it.each([
    null,
    {},
    { authenticated: true },
    { ...session(), tenantId: 'another-tenant' },
    { ...session(), objectId: 'an-email@example.invalid' },
    { ...session(), expiresAt: 'invalid' },
    { ...session(), expiresAt: '2000-01-01T00:00:00Z' },
  ])('rejects an incomplete or invalid success response: %j', async body => {
    fetchMock.mockResolvedValue(Response.json(body))
    await expect(call()).rejects.toMatchObject({ kind: 'unavailable' })
  })

  it('reports HTML responses and network errors without a successful fallback', async () => {
    fetchMock.mockResolvedValueOnce(new Response('<html>not the API</html>'))
    await expect(call()).rejects.toBeInstanceOf(OnlineApiError)
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    await expect(call()).rejects.toMatchObject({ kind: 'unavailable' })
  })

  it('times out the entire request and releases its timer', async () => {
    vi.useFakeTimers()
    fetchMock.mockImplementation((_url, options) => new Promise((_resolve, reject) => {
      options?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))
    }))
    const outcome = expect(call()).rejects.toMatchObject({ kind: 'unavailable' })
    await vi.advanceTimersByTimeAsync(15_000)
    await outcome
    expect(vi.getTimerCount()).toBe(0)
  })

  it('propagates a caller cancellation instead of displaying a spurious failure', async () => {
    const controller = new AbortController()
    fetchMock.mockImplementation((_url, options) => new Promise((_resolve, reject) => {
      options?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))
    }))
    const outcome = expect(call(controller.signal)).rejects.toMatchObject({ name: 'AbortError' })
    controller.abort()
    await outcome
  })
})
