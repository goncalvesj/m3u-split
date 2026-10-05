import { describe, expect, it } from 'vitest'
import { readOnlineConfiguration } from './online-config'

const environment = {
  BASE_URL: '/m3u-split/',
  VITE_ENTRA_TENANT_ID: '11111111-1111-4111-8111-111111111111',
  VITE_ENTRA_SPA_CLIENT_ID: '22222222-2222-4222-8222-222222222222',
  VITE_ENTRA_API_SCOPE: 'api://33333333-3333-4333-8333-333333333333/access_as_user',
  VITE_ONLINE_API_BASE_URL: 'https://function.example.invalid/api',
}
const pages = { origin: 'https://goncalvesj.github.io' }

describe('online public configuration', () => {
  it('leaves the local build usable with no online settings', () => {
    expect(readOnlineConfiguration({ BASE_URL: environment.BASE_URL }, pages)).toEqual({ kind: 'unconfigured' })
  })

  it('uses the exact Pages callback and a distinct explicit API URL', () => {
    expect(readOnlineConfiguration(environment, pages)).toEqual({
      kind: 'configured',
      config: {
        tenantId: environment.VITE_ENTRA_TENANT_ID,
        clientId: environment.VITE_ENTRA_SPA_CLIENT_ID,
        apiScope: environment.VITE_ENTRA_API_SCOPE,
        apiBaseUrl: 'https://function.example.invalid/api/',
        redirectUri: 'https://goncalvesj.github.io/m3u-split/auth.html',
      },
    })
  })

  it('supports the same base path on the local Vite server', () => {
    const result = readOnlineConfiguration({
      ...environment,
      VITE_ONLINE_API_BASE_URL: 'http://localhost:7071/api/',
    }, { origin: 'http://localhost:5173' })
    expect(result.kind).toBe('configured')
    if (result.kind !== 'configured') throw new Error('Expected local configuration')
    expect(result.config.redirectUri).toBe('http://localhost:5173/m3u-split/auth.html')
    expect(result.config.apiBaseUrl).toBe('http://localhost:7071/api/')
  })

  it.each([
    ['VITE_ENTRA_TENANT_ID', ''],
    ['VITE_ENTRA_TENANT_ID', 'common'],
    ['VITE_ENTRA_TENANT_ID', 'organizations'],
    ['VITE_ENTRA_TENANT_ID', '00000000-0000-0000-0000-000000000000'],
    ['VITE_ENTRA_SPA_CLIENT_ID', 'not-an-id'],
    ['VITE_ENTRA_API_SCOPE', ''],
    ['VITE_ENTRA_API_SCOPE', 'User.Read'],
    ['VITE_ENTRA_API_SCOPE', 'api://33333333-3333-4333-8333-333333333333/.default'],
    ['VITE_ENTRA_API_SCOPE', 'api://33333333-3333-4333-8333-333333333333/access_as_user another_scope'],
    ['VITE_ENTRA_API_SCOPE', 'api://22222222-2222-4222-8222-222222222222/access_as_user'],
    ['VITE_ONLINE_API_BASE_URL', ''],
    ['VITE_ONLINE_API_BASE_URL', '/api'],
    ['VITE_ONLINE_API_BASE_URL', '//function.example.invalid/api'],
    ['VITE_ONLINE_API_BASE_URL', 'https://goncalvesj.github.io/api/'],
    ['VITE_ONLINE_API_BASE_URL', 'http://function.example.invalid/api/'],
    ['VITE_ONLINE_API_BASE_URL', 'https://user:secret@function.example.invalid/api/'],
    ['VITE_ONLINE_API_BASE_URL', 'https://function.example.invalid/api/?code=key'],
    ['VITE_ONLINE_API_BASE_URL', 'https://function.example.invalid/api/#secret'],
    ['VITE_ONLINE_API_BASE_URL', 'javascript:alert(1)'],
  ])('fails closed for invalid %s: %s', (key, value) => {
    const result = readOnlineConfiguration({ ...environment, [key]: value }, pages)
    expect(result.kind).toBe('invalid')
    if (result.kind === 'invalid') expect(result.message).not.toBe('')
  })
})
